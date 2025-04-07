using UnityEngine;
using System;
[CreateAssetMenu(fileName = "New Voxel Data", menuName = "Custom/Voxel Data")]
public class VoxelDataSO : ScriptableObject
{
    public string voxelName;

    [Header("Voxel Properties")]
    public bool isTransparent = false;
    public bool isSolid = true;
    public int animationLength = 0;

    [Header("Face Data")]
    public int topTextureIndex;
    public int bottomTextureIndex;
    public int leftTextureIndex;
    public int rightTextureIndex;
    public int frontTextureIndex;
    public int backTextureIndex;
    public bool shareSides = false;
    public int sidesTextureIndex;

}