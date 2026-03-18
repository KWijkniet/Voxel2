using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

/// <summary>
/// Burst-compiled greedy mesher for a single 16³ chunk.
///
/// Produces two sets of geometry in one pass:
///   Vertices/Normals/UVs/UV2s/Triangles     — opaque blocks
///   TransVertices/…/TransTriangles           — transparent blocks (water, glass, …)
///
/// Transparent neighbour culling rules:
///   Opaque face  : hidden by other opaque blocks; visible through transparent.
///   Transparent face: hidden by same block type or any opaque; visible against air
///                     or a different transparent type.
///
/// Direction index convention (matches MeshBuilder.NeighbourDirs):
///   0 = +X, 1 = -X, 2 = +Y, 3 = -Y, 4 = +Z, 5 = -Z
/// </summary>
[BurstCompile]
public struct BuildChunkMeshJob : IJob
{
    private const int Size = VoxelChunk.Size; // 16

    // ── Inputs ────────────────────────────────────────────────────────────────
    [ReadOnly] public NativeArray<byte> Voxels;
    [ReadOnly] public NativeArray<byte> N_PX, N_NX, N_PY, N_NY, N_PZ, N_NZ;

    /// <summary>Bit i = 1 if neighbour i is a real loaded chunk (vs. zeroed placeholder).</summary>
    public int NeighbourMask;

    /// <summary>Voxels-per-cell: 1 for LOD 0, 2 for LOD 1, 4 for LOD 2, 8 for LOD 3.</summary>
    public int Step;

    // ── Opaque outputs (pre-allocated Persistent NativeLists) ─────────────────
    public NativeList<float3> Vertices;
    public NativeList<float3> Normals;
    public NativeList<float2> UVs;
    public NativeList<float2> UV2s;
    public NativeList<int>    Triangles;

    // ── Transparent outputs ───────────────────────────────────────────────────
    public NativeList<float3> TransVertices;
    public NativeList<float3> TransNormals;
    public NativeList<float2> TransUVs;
    public NativeList<float2> TransUV2s;
    public NativeList<int>    TransTriangles;

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
                byte neighborVoxel;

                if (neighborCell >= 0 && neighborCell < cells)
                {
                    pos[sliceAxis] = neighborCell * Step;
                    neighborVoxel  = Voxels[pos.x + pos.y * Size + pos.z * Size * Size];
                }
                else
                {
                    var nArr = NeighbourArray(dirIndex);
                    pos[sliceAxis] = neighborCell < 0 ? Size - Step : 0;
                    neighborVoxel  = nArr[pos.x + pos.y * Size + pos.z * Size * Size];

                    // Unloaded water boundary: hide face to avoid Z-fighting when neighbour loads.
                    if (here == BlockType.Water && ((NeighbourMask >> dirIndex) & 1) == 0)
                        neighborVoxel = BlockType.Water; // treat as same → hidden
                }

                mask[u + v * cells] = IsFaceHidden(here, neighborVoxel) ? (byte)0 : here;
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

                // Route to opaque or transparent output lists
                bool isTrans = BlockType.IsTransparent(blockType);
                var vList  = isTrans ? TransVertices  : Vertices;
                var nList  = isTrans ? TransNormals   : Normals;
                var uList  = isTrans ? TransUVs       : UVs;
                var u2List = isTrans ? TransUV2s      : UV2s;
                var tList  = isTrans ? TransTriangles : Triangles;

                int idx = vList.Length;
                vList.Add(corner);
                vList.Add(corner + du);
                vList.Add(corner + du + dv);
                vList.Add(corner + dv);

                nList.Add(normalVec); nList.Add(normalVec);
                nList.Add(normalVec); nList.Add(normalVec);

                uList.Add(new float2(0, 0));
                uList.Add(new float2(w, 0));
                uList.Add(new float2(w, h));
                uList.Add(new float2(0, h));

                float texSlice;
                if (blockType == BlockType.Log)
                    texSlice = math.abs(normalVec.y) > 0.5f ? BlockType.LogTopTile : BlockType.LogSideTile;
                else
                    texSlice = BlockType.GetAtlasTile(blockType);
                u2List.Add(new float2(texSlice, 0)); u2List.Add(new float2(texSlice, 0));
                u2List.Add(new float2(texSlice, 0)); u2List.Add(new float2(texSlice, 0));

                if (backFace)
                {
                    tList.Add(idx);   tList.Add(idx+2); tList.Add(idx+1);
                    tList.Add(idx);   tList.Add(idx+3); tList.Add(idx+2);
                }
                else
                {
                    tList.Add(idx);   tList.Add(idx+1); tList.Add(idx+2);
                    tList.Add(idx);   tList.Add(idx+2); tList.Add(idx+3);
                }

                for (int vv = 0; vv < h; vv++)
                for (int uu = 0; uu < w; uu++)
                    mask[u + uu + (v + vv) * cells] = 0;

                u += w;
            }
        }

        mask.Dispose();
    }

    // Returns true when a face between `here` and `neighbor` should be culled.
    private static bool IsFaceHidden(byte here, byte neighbor)
    {
        if (neighbor == BlockType.Air) return false;
        if (BlockType.IsTransparent(here))
            // Transparent: hidden by same block type or any opaque block
            return neighbor == here || !BlockType.IsTransparent(neighbor);
        // Opaque: hidden by other opaque blocks; transparent neighbours are see-through
        return !BlockType.IsTransparent(neighbor);
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
