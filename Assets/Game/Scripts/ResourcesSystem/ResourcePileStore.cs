using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Server-owned resource value. Visual rubble expiring cannot delete these records.</summary>
public sealed class ResourcePileStore
{
    [Serializable]
    public struct Pile
    {
        public long Id;
        public int MaterialId, ItemId;
        public double Units;
        public Vector3 Position;
    }
    private readonly Dictionary<long,Pile> _piles = new Dictionary<long,Pile>();
    private readonly Dictionary<Key,long> _buckets = new Dictionary<Key,long>();
    private readonly Dictionary<long,Key> _originalBuckets = new Dictionary<long,Key>();
    private long _nextId=1;
    private struct Key : IEquatable<Key>
    {
        public Vector3Int Cell; public int Material,Item;
        public bool Equals(Key o) => Cell==o.Cell && Material==o.Material && Item==o.Item;
        public override bool Equals(object o) => o is Key && Equals((Key)o);
        public override int GetHashCode() { unchecked { return (Cell.GetHashCode()*397^Material)*397^Item; } }
    }
    // One-metre resource buckets, separate from the terrain's voxel spacing.
    private static Key Bucket(Vector3 p,int material,int item) => new Key { Cell=Vector3Int.FloorToInt(p), Material=material,Item=item };
    public int Count => _piles.Count;
    public IEnumerable<Pile> All => _piles.Values;
    public bool TryGet(long id,out Pile p) => _piles.TryGetValue(id,out p);

    public long Add(int material,int item,double units,Vector3 position)
    {
        if (material<=0 || item<=0 || double.IsNaN(units) || double.IsInfinity(units) || units<=0 ||
            !Finite(position.x)||!Finite(position.y)||!Finite(position.z)) throw new ArgumentException("Invalid resource payload.");
        Key key=Bucket(position,material,item); long id; Pile p;
        if (_buckets.TryGetValue(key,out id) && _piles.TryGetValue(id,out p))
        {
            double total=p.Units+units;
            if (double.IsInfinity(total)) throw new OverflowException("Resource amount overflow.");
            // Keep the current physical position if this pile has already fallen.
            p.Units=total; _piles[id]=p; return id;
        }
        id=_nextId++;
        p=new Pile { Id=id,MaterialId=material,ItemId=item,Units=units,Position=position };
        _piles.Add(id,p); _buckets[key]=id; _originalBuckets[id]=key; return id;
    }
    private static bool Finite(float f) => !float.IsNaN(f)&&!float.IsInfinity(f);
    public bool Remove(long id,out Pile pile)
    {
        if (!_piles.TryGetValue(id,out pile)) return false;
        _piles.Remove(id); _buckets.Remove(_originalBuckets[id]); _originalBuckets.Remove(id); return true;
    }
    public bool UpdatePosition(long id,Vector3 position)
    {
        Pile p;
        if(!Finite(position.x)||!Finite(position.y)||!Finite(position.z)||!_piles.TryGetValue(id,out p))return false;
        p.Position=position;_piles[id]=p;return true;
    }
    public List<Pile> Copy() => new List<Pile>(_piles.Values);
    public void Import(IEnumerable<Pile> records)
    {
        foreach(Pile p in records) Add(p.MaterialId,p.ItemId,p.Units,p.Position);
    }
}
