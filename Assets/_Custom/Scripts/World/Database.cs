using UnityEngine;
using Custom.Importer;

public class Database
{
    public static WorldController worldController;

    private static Dictionary<Vector3, RegionController> regions;

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

    public static void RegisterRegion(Vector3 pos, RegionController region)
    {
        regions.add(pos, region);
    }

    public static RegionController GetRegion(Vector3 pos)
    {
        if (regions.has(pos))
        {
            return regions[pos];
        }
        return null;
    }
}
