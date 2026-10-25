using UnityEngine;

[CreateAssetMenu(fileName = "NewMineral", menuName = "MiningGame/Mineral Data")]
public class MineralData : ScriptableObject
{
    [Header("Mineral Properties")]
    public string mineralName;
    public int mineralID; // Unique ID used for networking/serialization
    public int baseValue = 50;
    public Color displayColor = Color.yellow;
    public GameObject dropPrefab;
}