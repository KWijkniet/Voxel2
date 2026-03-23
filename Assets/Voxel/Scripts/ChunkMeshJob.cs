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

    // ── Vegetation outputs (grass, flowers, twigs, … — LOD 0 only) ───────────
    /// <summary>Chunk coordinate in the voxel grid. Used to compute world XZ for per-column hash.</summary>
    public int3 ChunkCoord;
    public NativeList<float3> VegVertices;
    public NativeList<float3> VegNormals;
    public NativeList<float2> VegUVs;
    public NativeList<float2> VegUV2s;
    public NativeList<int>    VegTriangles;

    // ── IJob ──────────────────────────────────────────────────────────────────

    public void Execute()
    {
        GreedyFace(0, 1, 2, new float3( 1, 0, 0), false, 0); // +X
        GreedyFace(0, 1, 2, new float3(-1, 0, 0), true,  1); // -X
        GreedyFace(1, 2, 0, new float3( 0, 1, 0), false, 2); // +Y
        GreedyFace(1, 2, 0, new float3( 0,-1, 0), true,  3); // -Y
        GreedyFace(2, 0, 1, new float3( 0, 0, 1), false, 4); // +Z
        GreedyFace(2, 0, 1, new float3( 0, 0,-1), true,  5); // -Z
        EmitVegetation();
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

    // ── Vegetation emission ───────────────────────────────────────────────────

    /// <summary>
    /// Scans each XZ column for the topmost surface voxel (solid block with air above)
    /// and emits a 4-blade angled-splay tuft into the VegVertices/Normals/UVs/UV2s/Triangles lists.
    ///
    /// Only runs at LOD 0 (Step == 1). Higher LOD region meshes skip this pass.
    ///
    /// UV layout:  standard 0-1 quad mapping (texture sampled per blade face).
    /// UV2 layout: x = decoration atlas tile index (0 = grass); y = sway weight [0 at root, 1 at tip].
    ///             The VegetationLit shader reads UV2.y to drive wind displacement.
    /// </summary>
    private void EmitVegetation()
    {
        if (Step != 1) return;

        for (int lz = 0; lz < Size; lz++)
        for (int lx = 0; lx < Size; lx++)
        {
            int surfY = FindVegSurface(lx, lz);
            if (surfY < 0) continue;

            int  worldX = ChunkCoord.x * Size + lx;
            int  worldZ = ChunkCoord.z * Size + lz;
            uint h      = VegHash(worldX, worldZ);

            // Tuft centre: slight random XZ offset so adjacent tufts don't grid-align.
            float cx = lx + 0.5f + ((h        & 0xFF) / 255f - 0.5f) * 0.3f;
            float cz = lz + 0.5f + ((h >>  8  & 0xFF) / 255f - 0.5f) * 0.3f;
            float cy = surfY + 1f; // top face of the surface voxel

            // Height scale: [0.75, 1.0], rotation: [0°, 45°]
            float hs  = 0.75f + (h >> 16 & 0xFF) / 255f * 0.25f;
            float rot = (h >> 24 & 0xFF) / 255f * math.PI * 0.25f;

            math.sincos(rot, out float sr, out float cr);

            // Blade geometry constants
            float leanOut = 0.38f;        // horizontal reach of blade tip from centre
            float leanUp  = 0.75f * hs;  // vertical reach of blade tip
            float halfW   = 0.25f;        // half-width of each blade

            // 4 blades at 0°/90°/180°/270° in XZ, all rotated by `rot`.
            // leanX/leanZ give the direction each blade leans; widthX/widthZ are perpendicular.
            EmitBlade(cx, cy, cz,  cr * leanOut,  sr * leanOut, leanUp, -sr, cr, halfW);
            EmitBlade(cx, cy, cz, -sr * leanOut,  cr * leanOut, leanUp, -cr,-sr, halfW);
            EmitBlade(cx, cy, cz, -cr * leanOut, -sr * leanOut, leanUp,  sr,-cr, halfW);
            EmitBlade(cx, cy, cz,  sr * leanOut, -cr * leanOut, leanUp,  cr, sr, halfW);
        }
    }

    /// <summary>
    /// Returns the local Y of the topmost solid block whose block-above is air.
    /// "Solid" = any non-air block (water, leaves, stone, grass, etc. all qualify).
    /// Returns -1 if no surface found in this column.
    /// </summary>
    private int FindVegSurface(int lx, int lz)
    {
        for (int ly = Size - 1; ly >= 0; ly--)
        {
            byte b = Voxels[lx + ly * Size + lz * Size * Size];
            if (b == BlockType.Air) continue;

            byte above;
            if (ly < Size - 1)
                above = Voxels[lx + (ly + 1) * Size + lz * Size * Size];
            else
                above = N_PY[lx + 0 * Size + lz * Size * Size]; // top of chunk → check +Y neighbour

            if (above == BlockType.Air) return ly;
        }
        return -1;
    }

    /// <summary>
    /// Emits one quad (4 verts, 2 tris) into the vegetation lists.
    ///
    ///   root left  v0 ──── v1  root right   (UV y = 0, UV2.y = 0: no sway)
    ///              │        │
    ///   tip  left  v3 ──── v2  tip  right   (UV y = 1, UV2.y = 1: full sway)
    ///
    /// The shader is two-sided (Cull Off), so only one winding order is needed.
    ///
    /// Parameters:
    ///   cx/cy/cz        — tuft base (centre of top face of surface voxel)
    ///   dx/dz           — horizontal lean of the blade tip (XZ offset from base)
    ///   dy              — vertical rise of the blade tip
    ///   wx/wz           — half-width direction vector (perpendicular to lean, unit length)
    ///   halfW           — half-width scalar
    /// </summary>
    private void EmitBlade(float cx, float cy, float cz,
                            float dx, float dz, float dy,
                            float wx, float wz, float halfW)
    {
        int   idx  = VegVertices.Length;
        float3 up  = new float3(0, 1, 0);

        // Root: two verts side-by-side at the base of the blade
        VegVertices.Add(new float3(cx - wx * halfW, cy,      cz - wz * halfW));
        VegVertices.Add(new float3(cx + wx * halfW, cy,      cz + wz * halfW));
        // Tip: two verts shifted by the lean vector
        VegVertices.Add(new float3(cx + wx * halfW + dx, cy + dy, cz + wz * halfW + dz));
        VegVertices.Add(new float3(cx - wx * halfW + dx, cy + dy, cz - wz * halfW + dz));

        VegNormals.Add(up); VegNormals.Add(up); VegNormals.Add(up); VegNormals.Add(up);

        // UV: standard 0→1 quad (sampled from grass texture)
        VegUVs.Add(new float2(0, 0)); VegUVs.Add(new float2(1, 0));
        VegUVs.Add(new float2(1, 1)); VegUVs.Add(new float2(0, 1));

        // UV2: x = atlas tile index (0 = grass), y = sway weight
        float2 rootUV2 = new float2(0, 0); // root: no sway
        float2 tipUV2  = new float2(0, 1); // tip: full sway
        VegUV2s.Add(rootUV2); VegUV2s.Add(rootUV2);
        VegUV2s.Add(tipUV2);  VegUV2s.Add(tipUV2);

        // Two triangles, counter-clockwise front face (shader is two-sided)
        VegTriangles.Add(idx);     VegTriangles.Add(idx + 1); VegTriangles.Add(idx + 2);
        VegTriangles.Add(idx);     VegTriangles.Add(idx + 2); VegTriangles.Add(idx + 3);
    }

    /// <summary>
    /// Deterministic per-column hash. Drives tuft offset, height, and rotation variation.
    /// Identical to ChunkDecorator.Hash — keep them in sync.
    /// </summary>
    private static uint VegHash(int x, int z)
    {
        uint h = (uint)(x * 374761393 + z * 668265263);
        h ^= h >> 13;
        h *= 1274126177u;
        h ^= h >> 16;
        return h;
    }
}
