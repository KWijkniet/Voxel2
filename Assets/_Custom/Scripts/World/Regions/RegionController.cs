using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using Custom.Importer;

public class RegionController : MonoBehaviour
{
}

public class Chunk
{
    public bool hasCalculated = false;
    public bool hasGenerated = false;

    private byte[] voxels;
    private Mesh greedyMesh;    
    private Mesh detailMesh;

    public void Calculate()
    {

    }

    public void Clear()
    {
        if (greedyMesh != null) greedyMesh.Clear();
        greedyMesh = null;
        if (detailMesh != null) detailMesh.Clear();
        detailMesh = null;
        
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
}
