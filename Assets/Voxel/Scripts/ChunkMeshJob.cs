using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

/// <summary>
/// Burst-compiled greedy mesher for a single 16³ chunk.
///
/// Single-pass design: runs the greedy merge once per face direction, appending quads
/// to NativeLists via NativeList.Add. NativeLists are pre-allocated with generous
/// capacities so reallocation is rare in practice.
///
/// Direction index convention (matches MeshBuilder.NeighbourDirs):
///   0 = +X, 1 = -X, 2 = +Y, 3 = -Y, 4 = +Z, 5 = -Z
/// </summary>
[BurstCompile]
public struct BuildChunkMeshJob : IJob
{
    private const int   Size          = VoxelChunk.Size; // 16
    private const float AtlasTileCount = BlockType.AtlasTileCount;

    // ── Inputs ────────────────────────────────────────────────────────────────
    [ReadOnly] public NativeArray<byte> Voxels;
    [ReadOnly] public NativeArray<byte> N_PX, N_NX, N_PY, N_NY, N_PZ, N_NZ;

    /// <summary>Bit i = 1 if neighbour i is a real loaded chunk (vs. zeroed placeholder).</summary>
    public int NeighbourMask;

    /// <summary>Voxels-per-cell: 1 for LOD 0, 2 for LOD 1, 4 for LOD 2, 8 for LOD 3.</summary>
    public int Step;

    // ── Outputs (pre-allocated Persistent NativeLists) ────────────────────────
    public NativeList<float3> Vertices;
    public NativeList<float3> Normals;
    public NativeList<float2> UVs;
    public NativeList<int>    Triangles;

    // ── IJob ──────────────────────────────────────────────────────────────────

    public void Execute()
    {
        GreedyFace(0, 1, 2, new float3( 1, 0, 0), false, 0); // +X
        GreedyFace(0, 1, 2, new float3(-1, 0, 0), true,  1); // -X
        GreedyFace(1, 2, 0, new float3( 0, 1, 0), false, 2); // +Y
        GreedyFace(1, 2, 0, new float3( 0,-1, 0), true,  3); // -Y
        GreedyFace(2, 0, 1, new float3( 0, 0, 1), false, 4); // +Z
        GreedyFace(2, 0, 1, new float3( 0, 0,-1), true,  5); // -Z
    }

    // ── Greedy mesher ─────────────────────────────────────────────────────────

    private void GreedyFace(int sliceAxis, int uAxis, int vAxis,
                             float3 normalVec, bool backFace, int dirIndex)
    {
        int cells = Size / Step;

        // Per-slice mask. Allocator.Temp = fast per-thread allocator inside Burst jobs.
        var mask = new NativeArray<byte>(cells * cells, Allocator.Temp, NativeArrayOptions.ClearMemory);
        var pos  = new int3();

        for (int slice = 0; slice < cells; slice++)
        {
            // ── Build mask ────────────────────────────────────────────────────
            for (int v = 0; v < cells; v++)
            for (int u = 0; u < cells; u++)
            {
                pos[sliceAxis] = slice * Step;
                pos[uAxis]     = u     * Step;
                pos[vAxis]     = v     * Step;

                byte here = Voxels[pos.x + pos.y * Size + pos.z * Size * Size];
                if (here == 0) { mask[u + v * cells] = 0; continue; }

                int  neighborCell = slice + (backFace ? -1 : 1);
                bool neighborSolid;

                if (neighborCell >= 0 && neighborCell < cells)
                {
                    pos[sliceAxis] = neighborCell * Step;
                    neighborSolid  = Voxels[pos.x + pos.y * Size + pos.z * Size * Size] != 0;
                }
                else
                {
                    var nArr = NeighbourArray(dirIndex);
                    pos[sliceAxis] = neighborCell < 0 ? Size - Step : 0;
                    neighborSolid  = nArr[pos.x + pos.y * Size + pos.z * Size * Size] != 0;

                    // Water at an unloaded boundary: hide face to prevent Z-fighting when
                    // the neighbour later loads and renders its own water face.
                    if (here == BlockType.Water && ((NeighbourMask >> dirIndex) & 1) == 0)
                        neighborSolid = true;
                }

                mask[u + v * cells] = neighborSolid ? (byte)0 : here;
            }

            // ── Greedy merge + emit quads ─────────────────────────────────────
            for (int v = 0; v < cells; v++)
            for (int u = 0; u < cells;)
            {
                byte blockType = mask[u + v * cells];
                if (blockType == 0) { u++; continue; }

                int w = 1;
                while (u + w < cells && mask[u + w + v * cells] == blockType) w++;

                int h = 1; bool done = false;
                while (v + h < cells && !done)
                {
                    for (int k = 0; k < w; k++)
                        if (mask[u + k + (v + h) * cells] != blockType) { done = true; break; }
                    if (!done) h++;
                }

                pos[sliceAxis] = slice * Step + (backFace ? 0 : Step);
                pos[uAxis]     = u     * Step;
                pos[vAxis]     = v     * Step;

                var corner = new float3(pos.x, pos.y, pos.z);
                var du = float3.zero; du[uAxis] = w * Step;
                var dv = float3.zero; dv[vAxis] = h * Step;

                int idx = Vertices.Length;
                Vertices.Add(corner);
                Vertices.Add(corner + du);
                Vertices.Add(corner + du + dv);
                Vertices.Add(corner + dv);

                Normals.Add(normalVec); Normals.Add(normalVec);
                Normals.Add(normalVec); Normals.Add(normalVec);

                float tileU = (blockType - 1 + 0.5f) / AtlasTileCount;
                UVs.Add(new float2(tileU, 0f)); UVs.Add(new float2(tileU, 0f));
                UVs.Add(new float2(tileU, 1f)); UVs.Add(new float2(tileU, 1f));

                if (backFace)
                {
                    Triangles.Add(idx);   Triangles.Add(idx+2); Triangles.Add(idx+1);
                    Triangles.Add(idx);   Triangles.Add(idx+3); Triangles.Add(idx+2);
                }
                else
                {
                    Triangles.Add(idx);   Triangles.Add(idx+1); Triangles.Add(idx+2);
                    Triangles.Add(idx);   Triangles.Add(idx+2); Triangles.Add(idx+3);
                }

                for (int vv = 0; vv < h; vv++)
                for (int uu = 0; uu < w; uu++)
                    mask[u + uu + (v + vv) * cells] = 0;

                u += w;
            }
        }

        mask.Dispose();
    }

    private NativeArray<byte> NeighbourArray(int dirIndex)
    {
        switch (dirIndex)
        {
            case 0: return N_PX;
            case 1: return N_NX;
            case 2: return N_PY;
            case 3: return N_NY;
            case 4: return N_PZ;
            default: return N_NZ;
        }
    }
}
