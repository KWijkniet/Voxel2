using System.Collections.Generic;
using UnityEngine;

[ExecuteInEditMode]
public class ChunkCalculator : MonoBehaviour
{
    public Database database;
    public int size = 16;
    public int maxY = 8;
    public bool useGreedyMesh = false;
    public bool useRandomVoxels = false;
    [Range(0, 256)]
    public int voxelIndex = 0;
    [Range(0f, 1f)]
    public float percentage = 0.5f;

    private byte[] voxels;

    public List<Material> materials;

    private Mesh greedyMesh;    
    private Mesh detailMesh;
    private List<RenderParams> renderParams;

    private void Update()
    {
        if(greedyMesh != null)
        {
            Graphics.RenderMesh(renderParams[0], greedyMesh, 0, Matrix4x4.identity);
            Graphics.RenderMesh(renderParams[1], greedyMesh, 1, Matrix4x4.identity);
        }
        if(detailMesh != null)
        {
            Graphics.RenderMesh(renderParams[0], detailMesh, 0, Matrix4x4.identity);
        }
    }

    public void Generate()
    {
        Clear();
        if (database == null || database.VoxelCount() == 0 || (!useRandomVoxels && voxelIndex >= database.VoxelCount()))
        {
            Debug.LogError("Invalid database or voxel index");
            return;
        }

        voxels = new byte[size*size*size];
        int index = 0;
        for (int z = 0; z < size; z++)
        {
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    voxels[index] = (byte)(useRandomVoxels ? Random.Range(0f, 1f) * (database.VoxelCount() - 1) + 1 : voxelIndex);
                    // voxels[index] = (byte)(y < maxY ? Random.Range(0f, 1f) > percentage ? useRandomVoxels ? Random.Range(0f, 1f) * (database.VoxelCount() - 1) + 1 : voxelIndex : 0 : 0);
                    if(y > 0) voxels[index] = 0;
                    index++;
                }
            }
        }

        // for (int i = 0; i < voxels.Length; i++)
        // {
        //     int y = (i / size) % size;
        //     voxels[i] = (byte)(y < maxY ? Random.Range(0f, 1f) > percentage ? useRandomVoxels ? Random.Range(0f, 1f) * (database.VoxelCount() - 1) + 1 : voxelIndex : 0 : 0);
        // }

        GreedyMesher gm = new GreedyMesher(database, voxels, size, size, size);
        if (greedyMesh != null) greedyMesh.Clear();
        greedyMesh = gm.GenerateMesh();

        // // Details
        // DetailMesher dm = new DetailMesher(database, voxels, size, size, size);
        // if (detailMesh != null) detailMesh.Clear();
        // detailMesh = dm.GenerateMesh();
    }

    public void Clear()
    {
        if (greedyMesh != null) greedyMesh.Clear();
        greedyMesh = null;
        if (detailMesh != null) detailMesh.Clear();
        detailMesh = null;

        database.Import();
        
        foreach (Material material in materials)
        {
            
            // Set the buffer and texture array to the material
            material.SetBuffer("_VoxelBuffer", database.GetVoxelBuffer());
            material.SetTexture("_MainTex", database.GetTexture2DArray());
        }

        renderParams = new List<RenderParams>();
        for (int i = 0; i < 2; i++)
        {
            RenderParams renderParam = new RenderParams(materials[i]);
            renderParam.instanceID = gameObject.GetInstanceID();
            renderParam.layer = 1;
            renderParam.receiveShadows = true;
            renderParam.renderingLayerMask = 1;
            renderParam.rendererPriority = 1;
            renderParam.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            renderParams.Add(renderParam);
        }
    }
}
