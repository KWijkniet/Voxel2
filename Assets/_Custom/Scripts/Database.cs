using UnityEngine;
using Custom.Importer;

[CreateAssetMenu(fileName = "New Database", menuName = "Custom/Database")]
public class Database : ScriptableObject
{
    public int VoxelCount()
    {
        ImportController importController = ImportController.GetInstance();
        return importController.voxels.Count;
    }

    public JSONData GetVoxelData(int index)
    {
        ImportController importController = ImportController.GetInstance();
        foreach (JSONData voxel in importController.voxels)
        {
            if (int.Parse(voxel.id) == index)
            {
                return voxel;
            }
        }
        return null;
    }

    public Texture2DArray GetTexture2DArray()
    {
        ImportController importController = ImportController.GetInstance();
        return importController.texture2DArray;
    }

    public int GetTextureIndex(string path)
    {
        ImportController importController = ImportController.GetInstance();
        foreach (TextureData textureData in importController.textures)
        {
            if (textureData.path == path)
            {
                Debug.Log("Texture found: " + textureData.index);
                return textureData.index;
            }
        }
        Debug.LogError("Texture not found: " + path);
        return -1;
    }

    public ComputeBuffer GetVoxelBuffer()
    {
        ImportController importController = ImportController.GetInstance();
        return importController.voxelBuffer;
    }

    public void Import()
    {
        ImportController importController = ImportController.GetInstance();
        importController.Import();
    }
}
