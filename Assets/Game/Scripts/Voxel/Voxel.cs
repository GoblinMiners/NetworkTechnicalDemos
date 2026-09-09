using UnityEngine;

public struct Voxel

{

    public Vector3 pos;
    public bool isActive;
    public Color Color;
    
    public Voxel(Vector3 pos, Color color,  bool isActive = true)
    {
        this.pos = pos;
        this.isActive = isActive;
        this.Color = color;
    }
    


}
