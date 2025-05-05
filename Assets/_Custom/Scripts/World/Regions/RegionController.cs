using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using Custom.Importer;

public class RegionController : MonoBehaviour
{
}

public class Chunk
{
    private Vector3Int worldPos;
    private byte[] voxels;

    public Mesh greedyMesh;
    public Mesh detailMesh;
    private int[,] heightmap;
    private Vector3Int position;
    private Matrix4x4 matrix;

    public Chunk(Vector3Int worldPos, int[,] heightmap){
        this.matrix = Matrix4x4.TRS(worldPos, Quaternion.identity, Vector3.one);
        this.worldPos = worldPos;
        this.heightmap = heightmap;

        // bool skip = true;
        // for (int x = 0; x < Database.worldController.width; x++)
        // {
        //     for (int z = 0; z < Database.worldController.depth; z++)
        //     {
        //         int height = heightmap[x, z];
        //         if (height + 1 >= worldPos.y && height - 1 < worldPos.y + Database.worldController.height) 
        //         {
        //             skip = false;
        //             break;
        //         }
        //     }
        //     if (skip) break;
        // }

        // if (skip) return;
        // Debug.Log("Valid Chunk: " + worldPos.y);
        Calculate();
        Generate();
    }

    public void Generate()
    {
        GreedyMesher gm = new GreedyMesher(voxels, Database.worldController.width, Database.worldController.height, Database.worldController.depth);
        if (greedyMesh != null) greedyMesh.Clear();
        greedyMesh = gm.GenerateMesh();

        // Details
        DetailMesher dm = new DetailMesher(voxels, Database.worldController.width, Database.worldController.height, Database.worldController.depth);
        if (detailMesh != null) detailMesh.Clear();
        detailMesh = dm.GenerateMesh();
    }

    public void Render()
    {
        if(greedyMesh != null)
        {
            Graphics.RenderMesh(Database.worldController.renderParams[0], greedyMesh, 0, matrix);
            Graphics.RenderMesh(Database.worldController.renderParams[1], greedyMesh, 1, matrix);
        }

        if(detailMesh != null)
        {
            Graphics.RenderMesh(Database.worldController.renderParams[2], detailMesh, 0, matrix);
            Graphics.RenderMesh(Database.worldController.renderParams[2], detailMesh, 1, matrix);
        }
    }

    public void Calculate()
    {
        voxels = new byte[Database.worldController.width * Database.worldController.height * Database.worldController.depth];

        // Calculate
        for (int z = 0; z < Database.worldController.width; z++)
        {
            for (int y = 0; y < Database.worldController.height; y++)
            {
                for (int x = 0; x < Database.worldController.depth; x++)
                {
                    int maxHeight = heightmap[x,z];
                    int index = Helpers.CoordinatesToIndex(x, y, z, Database.worldController.width, Database.worldController.height, Database.worldController.depth);
                    if (index < 0) Debug.LogWarning("Chunk: Voxel not found");

                    voxels[index] = 0;
                    if (worldPos.y + y <= maxHeight)
                    {
                        if (worldPos.y + y < 8) voxels[index] = (byte)1;
                        else if (worldPos.y + y <= maxHeight) voxels[index] = (byte)2;
                        
                        if (worldPos.y + y == maxHeight && worldPos.y + y >= Database.worldController.waterLevel) voxels[index] = (byte)4;
                    }
                    else if (worldPos.y + y <= Database.worldController.waterLevel) voxels[index] = (byte)3;
                    else if (worldPos.y + y == maxHeight + 1 && Random.Range(0f, 1f) > 0.75f) voxels[index] = (byte)5;
                }
            }
        }
    }

    public void Clear()
    {
        if (greedyMesh != null) greedyMesh.Clear();
        greedyMesh = null;
        if (detailMesh != null) detailMesh.Clear();
        detailMesh = null;
    }
}
