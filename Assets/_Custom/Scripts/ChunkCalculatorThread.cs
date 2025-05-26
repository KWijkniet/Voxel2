using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using Custom.Importer;

[ExecuteInEditMode]
public class ChunkCalculatorThread : MonoBehaviour
{
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
            Graphics.RenderMesh(renderParams[2], detailMesh, 0, Matrix4x4.identity);
            Graphics.RenderMesh(renderParams[2], detailMesh, 1, Matrix4x4.identity);
        }
    }

    public void Generate()
    {
        Clear();
        if (Database.VoxelCount() == 0 || (!useRandomVoxels && voxelIndex >= Database.VoxelCount()))
        {
            Debug.LogError("Invalid database or voxel index");
            return;
        }

        voxels = new byte[size*size*size];
        CalculateChunk();

        GreedyMesher gm = new GreedyMesher(voxels, size, size, size);
        if (greedyMesh != null) greedyMesh.Clear();
        greedyMesh = gm.GenerateMesh();

        // Details
        DetailMesher dm = new DetailMesher(voxels, size, size, size);
        if (detailMesh != null) detailMesh.Clear();
        detailMesh = dm.GenerateMesh();
    }

    public void Clear()
    {
        if (greedyMesh != null) greedyMesh.Clear();
        greedyMesh = null;
        if (detailMesh != null) detailMesh.Clear();
        detailMesh = null;

        Database.Import();
        
        foreach (Material material in materials)
        {
            // Set the buffer and texture array to the material
            material.SetBuffer("_VoxelBuffer", Database.GetVoxelBuffer());
            material.SetTexture("_MainTex", Database.GetTexture2DArray());
        }

        renderParams = new List<RenderParams>();
        for (int i = 0; i < materials.Count; i++)
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

    private void CalculateChunk(){
        int centerY = 0;
        for (int z = 0; z < size; z++)
        {
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int maxHeight = Mathf.RoundToInt(Mathf.PerlinNoise(x * 0.1f, z * 0.1f) * 16f);
                    int index = Helpers.CoordinatesToIndex(x, y, z, size, size, size);
                    if (index < 0) Debug.LogWarning("ChunkCalculator: Voxel not found");
                    voxels[index] = 0;

                    if (y <= maxHeight)
                    {
                        if (y < 8) voxels[index] = (byte)1;
                        else if (y <= maxHeight) voxels[index] = (byte)2;
                        
                        if (y == maxHeight && y >= maxY) voxels[index] = (byte)4;

                        if(x == 8 && z == 8 && y == maxHeight) centerY = y < maxY ? maxY : maxHeight;
                    }
                    else if (y <= maxY) voxels[index] = (byte)3;
                    else if (y == maxHeight + 1 && Random.Range(0f, 1f) > 0.75f) voxels[index] = (byte)5;
                }
            }
        }

        //Place tree
        ApplyStructure(1, 1, 8, centerY, 8);
    }

    private void ApplyStructure(int id, int variantId, int centerX, int centerY, int centerZ)
    {
        JSONStructure structure = Database.GetStructure(id);
        if(structure == null) return;
        StructureVariant variant = structure.GetVariant(variantId);
        
        for (int x = centerX - variant.center[0]; x < centerX - variant.center[0] + variant.width; x++)
        {
            for (int y = centerY - variant.center[1]; y < centerY - variant.center[1] + variant.height; y++)
            {
                for (int z = centerZ - variant.center[2]; z < centerZ - variant.center[2] + variant.depth; z++)
                {
                    int index = Helpers.CoordinatesToIndex(x, y, z, size, size, size);
                    if (index < 0) {  Debug.LogWarning("ChunkCalculator: Voxel not found"); continue; }

                    // Target voxel        
                    int localX = x - (centerX - variant.center[0]);
                    int localY = y - (centerY - variant.center[1]);
                    int localZ = z - (centerZ - variant.center[2]);            
                    int target = localX + localY * variant.width + localZ * variant.width * variant.height;
                    
                    // Voxel
                    byte voxel = (byte)variant.voxels[target];
                    if (voxel == 0) continue;

                    voxels[index] = voxel;
                }
            }
        }
    }
}
