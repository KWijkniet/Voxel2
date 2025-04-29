using UnityEngine;
using System.Collections.Generic;

public class WorldController : MonoBehaviour
{
    public int width = 16, height = 16, depth = 16;
    public int worldHeight;
    public int waterLevel = 0;
    public int renderDistance = 1;
    public List<Material> materials;

    public List<RenderParams> renderParams;
    private Dictionary<Vector2Int, Region> regions = new Dictionary<Vector2Int, Region>();

    private void Awake()
    {
        Database.worldController = this;
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

        // Initial load
        for (int x = -renderDistance; x < renderDistance + 1; x++)
        {
            for (int z = -renderDistance; z < renderDistance + 1; z++)
            {
                Debug.Log(x + ", " + z);

                Vector2Int key = new Vector2Int(x, z);
                if (!regions.TryGetValue(key, out var region))
                {
                    regions[key] = new Region(new Vector2Int(x * width, z * depth));
                }
            }
        }
    }

    private void Update()
    {
        foreach(KeyValuePair<Vector2Int, Region> entry in regions)
        {
            entry.Value.Render();
        }
    }

    // public void AddRegion(Vector2Int pos, Region region)
    // {
    //     regions.Add(pos, region);
    // }

    // public void GetRegion(Vector2Int pos, Region region)
    // {
    //     if (regions.has(pos))
    //     {
    //         return regions[pos];
    //     }
    //     return null;
    // }
}
