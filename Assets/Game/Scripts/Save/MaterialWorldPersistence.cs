using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Security.Cryptography;
using UnityEngine;

/// <summary>Versioned sparse terrain + uncollected resources in a single checksummed snapshot.</summary>
public static class MaterialWorldPersistence
{
    private const string Magic = "GMBMAT01";
    public const int FormatVersion = 3;
    public struct DensityPoint { public Vector3Int Position; public float Value; }
    public sealed class Snapshot
    {
        public int Seed;
        public Vector3Int ChunkSize;
        public float VoxelSize;
        public string RulesSignature;
        public List<DensityPoint> Density = new List<DensityPoint>();
        public List<ResourcePileStore.Pile> Resources = new List<ResourcePileStore.Pile>();
        public bool ExpeditionState;
        public int ExpeditionUnlocks;
        public List<GobmineFluidPoint> Fluids = new List<GobmineFluidPoint>();
        public List<TerrainShapeRecord> Shapes = new List<TerrainShapeRecord>();
    }
    public static string DefaultPath => Path.Combine(Application.persistentDataPath,"world_materials_v1.dat");

    public static bool TryLoad(string path,out Snapshot snapshot,out string error)
    {
        snapshot=null; error=null;
        bool found=false;
        foreach (string candidate in new[]{path,path+".bak"})
        {
            if (!File.Exists(candidate)) continue;
            found=true;
            try
            {
                snapshot=Decode(File.ReadAllBytes(candidate));
                if (candidate!=path) Debug.LogWarning("[Material Save] Recovered backup after primary save failed: "+error);
                error=null; return true;
            }
            catch(Exception ex) { error=ex.Message; }
        }
        if (found) error="No valid material save or backup: "+error;
        return false;
    }

    public static void Write(string path,Snapshot snapshot)
    {
        byte[] bytes=Encode(snapshot);
        string directory=Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temp=path+".tmp", backup=path+".bak";
        using(var stream=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None))
        { stream.Write(bytes,0,bytes.Length); stream.Flush(true); }
        if (File.Exists(path)) File.Replace(temp,path,backup);
        else File.Move(temp,path);
    }

    public static byte[] Encode(Snapshot s)
    {
        using(var buffer=new MemoryStream())
        {
            using(var w=new BinaryWriter(buffer,Encoding.UTF8,true))
            {
                int format = s.Shapes.Count > 0 ? FormatVersion :
                    s.ExpeditionState || s.ExpeditionUnlocks != 0 || s.Fluids.Count > 0 ? 2 : 1;
                w.Write(Encoding.ASCII.GetBytes(Magic)); w.Write(format); w.Write(TerrainMaterialCatalog.GeneratorVersion);
                w.Write(s.Seed); WritePosition(w,s.ChunkSize); w.Write(s.VoxelSize); w.Write(s.RulesSignature ?? "");
                w.Write(s.Density.Count);
                foreach(var p in s.Density) { WritePosition(w,p.Position); w.Write(p.Value); }
                w.Write(s.Resources.Count);
                foreach(var p in s.Resources)
                { w.Write(p.MaterialId); w.Write(p.ItemId); w.Write(p.Units); w.Write(p.Position.x); w.Write(p.Position.y); w.Write(p.Position.z); }
                if (format >= 2)
                {
                    w.Write(s.ExpeditionUnlocks); w.Write(s.Fluids.Count);
                    foreach (var f in s.Fluids) { WritePosition(w, f.Cell); w.Write((byte)f.Kind); w.Write(f.Amount); }
                }
                if (format >= 3)
                {
                    w.Write(s.ExpeditionState); // V3 can contain shapes without expedition mode.
                    w.Write(s.Shapes.Count);
                    foreach (var shape in s.Shapes)
                    {
                        w.Write(shape.Id); w.Write(shape.Ceiling);
                        WriteVector(w,shape.Position); WriteQuaternion(w,shape.Rotation);
                        WriteVector(w,shape.Velocity); WriteVector(w,shape.AngularVelocity);
                        w.Write(shape.Samples.Count);
                        foreach (var sample in shape.Samples)
                        { WritePosition(w,sample.Grid); w.Write(sample.Density); }
                    }
                }
            }
            byte[] payload=buffer.ToArray();
            using(var sha=SHA256.Create()) { byte[] hash=sha.ComputeHash(payload); buffer.Write(hash,0,hash.Length); }
            return buffer.ToArray();
        }
    }
    public static Snapshot Decode(byte[] bytes)
    {
        if (bytes==null || bytes.Length<80) throw new InvalidDataException("Save is truncated.");
        int payloadSize=bytes.Length-32;
        using(var sha=SHA256.Create())
        {
            byte[] hash=sha.ComputeHash(bytes,0,payloadSize);
            for(int i=0;i<32;i++) if(hash[i]!=bytes[payloadSize+i]) throw new InvalidDataException("Save checksum mismatch.");
        }
        using(var stream=new MemoryStream(bytes,0,payloadSize,false))
        using(var r=new BinaryReader(stream))
        {
            if(Encoding.ASCII.GetString(r.ReadBytes(8))!=Magic) throw new InvalidDataException("Unsupported save signature.");
            int format = r.ReadInt32();
            if ((format != 1 && format != 2 && format != FormatVersion) || r.ReadInt32()!=TerrainMaterialCatalog.GeneratorVersion)
                throw new InvalidDataException("Unsupported save/generator version.");
            var s=new Snapshot { Seed=r.ReadInt32(),ChunkSize=ReadPosition(r),VoxelSize=r.ReadSingle(),RulesSignature=r.ReadString() };
            if(s.ChunkSize.x<1 || s.ChunkSize.y<1 || s.ChunkSize.z<1 || !Finite(s.VoxelSize) || s.VoxelSize<=0 || s.RulesSignature.Length!=64)
                throw new InvalidDataException("Invalid save settings.");
            int count=r.ReadInt32(); CheckCount(count,16,stream);
            var seen=new HashSet<Vector3Int>();
            for(int i=0;i<count;i++)
            {
                var p=new DensityPoint { Position=ReadPosition(r),Value=r.ReadSingle() };
                if(!Finite(p.Value) || !seen.Add(p.Position)) throw new InvalidDataException("Invalid/duplicate terrain sample.");
                s.Density.Add(p);
            }
            count=r.ReadInt32(); CheckCount(count,28,stream);
            for(int i=0;i<count;i++)
            {
                var p=new ResourcePileStore.Pile {MaterialId=r.ReadInt32(),ItemId=r.ReadInt32(),Units=r.ReadDouble(),
                    Position=new Vector3(r.ReadSingle(),r.ReadSingle(),r.ReadSingle())};
                if(p.MaterialId<=0 || p.ItemId<=0 || p.Units<=0 || double.IsNaN(p.Units) || double.IsInfinity(p.Units) ||
                    !Finite(p.Position.x)||!Finite(p.Position.y)||!Finite(p.Position.z)) throw new InvalidDataException("Invalid resource record.");
                s.Resources.Add(p);
            }
            s.ExpeditionState = format >= 2;
            if (format >= 2)
            {
                s.ExpeditionUnlocks = r.ReadInt32();
                if ((s.ExpeditionUnlocks & ~511) != 0) throw new InvalidDataException("Invalid expedition unlocks.");
                count = r.ReadInt32(); CheckCount(count, 17, stream);
                seen.Clear();
                for (int i = 0; i < count; i++)
                {
                    var f = new GobmineFluidPoint { Cell = ReadPosition(r), Kind = (GobmineFluidKind)r.ReadByte(), Amount = r.ReadInt32() };
                    if (!seen.Add(f.Cell) || (int)f.Kind > 2 || f.Amount < 0 || f.Amount > GobmineFluidStore.Capacity ||
                        (f.Amount > 0 && f.Kind == GobmineFluidKind.None) || (f.Amount == 0 && f.Kind != GobmineFluidKind.None))
                        throw new InvalidDataException("Invalid/duplicate fluid state.");
                    s.Fluids.Add(f);
                }
            }
            if (format >= 3)
            {
                s.ExpeditionState = r.ReadBoolean();
                count=r.ReadInt32(); CheckCount(count,61,stream);
                var ids=new HashSet<int>();
                for(int i=0;i<count;i++)
                {
                    var shape=new TerrainShapeRecord { Id=r.ReadInt32(),Ceiling=r.ReadBoolean(),
                        Position=ReadVector(r),Rotation=ReadQuaternion(r),
                        Velocity=ReadVector(r),AngularVelocity=ReadVector(r) };
                    if(shape.Id<=0 || !ids.Add(shape.Id) || !Finite(shape.Position.x) ||
                       !Finite(shape.Position.y) || !Finite(shape.Position.z) ||
                       !Finite(shape.Rotation.x) || !Finite(shape.Rotation.y) ||
                       !Finite(shape.Rotation.z) || !Finite(shape.Rotation.w) ||
                       !Finite(shape.Velocity.x) || !Finite(shape.Velocity.y) || !Finite(shape.Velocity.z) ||
                       !Finite(shape.AngularVelocity.x) || !Finite(shape.AngularVelocity.y) || !Finite(shape.AngularVelocity.z))
                        throw new InvalidDataException("Invalid shape transform/ID.");
                    int samples=r.ReadInt32(); CheckCount(samples,16,stream);
                    if (samples<1 || samples>2000000) throw new InvalidDataException("Invalid shape sample count.");
                    var seenSamples=new HashSet<Vector3Int>();
                    bool anySolid = false;
                    for(int j=0;j<samples;j++)
                    {
                        Vector3Int pos=ReadPosition(r); float density=r.ReadSingle();
                        if(!Finite(density) || !seenSamples.Add(pos))
                            throw new InvalidDataException("Invalid/duplicate detached sample.");
                        if (density < 0f) anySolid = true;
                        shape.Samples.Add(new TerrainShapeSample(pos,density));
                    }
                    if (!anySolid) throw new InvalidDataException("Detached shape has no solid samples.");
                    s.Shapes.Add(shape);
                }
            }
            if(stream.Position!=stream.Length) throw new InvalidDataException("Unexpected save trailing data.");
            return s;
        }
    }
    private static bool Finite(float f)=>!float.IsNaN(f)&&!float.IsInfinity(f);
    private static void CheckCount(int count,int stride,Stream stream)
    { if(count<0 || count>10000000 || (long)count*stride>stream.Length-stream.Position) throw new InvalidDataException("Invalid record count."); }
    private static void WriteVector(BinaryWriter w,Vector3 v) {w.Write(v.x);w.Write(v.y);w.Write(v.z);}
    private static Vector3 ReadVector(BinaryReader r)=>new Vector3(r.ReadSingle(),r.ReadSingle(),r.ReadSingle());
    private static void WriteQuaternion(BinaryWriter w,Quaternion q){w.Write(q.x);w.Write(q.y);w.Write(q.z);w.Write(q.w);}
    private static Quaternion ReadQuaternion(BinaryReader r)=>new Quaternion(r.ReadSingle(),r.ReadSingle(),r.ReadSingle(),r.ReadSingle());
    private static void WritePosition(BinaryWriter w,Vector3Int p) {w.Write(p.x);w.Write(p.y);w.Write(p.z);}
    private static Vector3Int ReadPosition(BinaryReader r)=>new Vector3Int(r.ReadInt32(),r.ReadInt32(),r.ReadInt32());
}
