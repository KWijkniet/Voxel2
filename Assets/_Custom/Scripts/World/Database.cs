using UnityEngine;
using Custom.Importer;
using System.Collections.Generic;

public class Database
{
    public static WorldController worldController;

    public static int VoxelCount()
    {
        ImportController importController = ImportController.GetInstance();
        return importController.voxels.Count;
    }

    public static JSONData GetVoxelData(int index)
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

    public static JSONStructure GetStructure(int index)
    {
        ImportController importController = ImportController.GetInstance();
        foreach (JSONStructure structure in importController.structures)
        {
            if (int.Parse(structure.id) == index)
            {
                return structure;
            }
        }
        return null;
    }

    public static Texture2DArray GetTexture2DArray()
    {
        ImportController importController = ImportController.GetInstance();
        return importController.texture2DArray;
    }

    public static int GetTextureIndex(string path)
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

    public static ComputeBuffer GetVoxelBuffer()
    {
        ImportController importController = ImportController.GetInstance();
        return importController.voxelBuffer;
    }

    public static void Import()
    {
        ImportController importController = ImportController.GetInstance();
        importController.Import();
    }

    // public static void RegisterRegion(Vector3 pos, Region region)
    // {
    //     worldController.AddRegion(pos, region);
    // }

    // public static Region GetRegion(Vector3 pos)
    // {
    //     return worldController.GetRegion(pos);
    // }
}
