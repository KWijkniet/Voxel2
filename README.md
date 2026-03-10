Real-life view distance in a voxel game is a substantial architectural challenge. Here's the full roadmap in order of dependency:

1. LOD System (the core requirement)
At 500m+ you cannot render every voxel at full resolution. You need progressively coarser meshes for distant chunks. Each LOD level halves the sampling resolution:

LOD	Distance	Effective resolution	Voxels sampled
0	0–2 chunks	16³ full	every voxel
1	2–8 chunks	8³	every 2nd
2	8–32 chunks	4³	every 4th
3	32–128 chunks	2³	every 8th
4	128+ chunks	1³	block colour only
The hard part: LOD boundary cracks — where a high-res chunk meets a low-res one, gaps appear. Solutions range from simple skirt geometry to the full Transvoxel algorithm.

2. Frustum Culling
Before generating or rendering a chunk, test its AABB against the camera frustum. At large view distances this skips 60–80% of chunks outside the FOV entirely — essentially free performance.

3. Distance-Sorted Generation Queue
Currently chunks are requested in arbitrary order. At large distances you need to sort the queue by distance from the player so the visible area fills in front-to-back, not randomly.

4. Larger Chunk Size for LOD
16³ chunks produce too many draw calls at scale. The typical approach:

Keep 16³ as the data unit (palette chunks stay small)
Introduce a Region (e.g. 4×4×4 chunks = 64³ voxels) as the LOD rendering unit
Higher LOD levels render one mesh per region instead of per chunk

5. Indirect / Batched Rendering
At viewDistance=128 chunks you'd have ~200,000 draw calls. You need:

Graphics.DrawMeshIndirect or GPU instancing
Or Unity's DOTS/ECS rendering pipeline
Recommended order of implementation

Frustum culling      ← easy, immediate gains
↓
Distance-sorted queue ← easy, improves streaming feel  
↓
LOD meshing (no cracks) ← medium, required for distance
↓
LOD crack fixing      ← hard, required for quality
↓
Region-based rendering ← hard, required for scale
↓
Indirect rendering    ← very hard, required for 2km+
The first two are small changes to existing code. LOD meshing is where the real work begins — want me to start with frustum culling + sorted queue as quick wins, or jump straight into the LOD system?

6. World generation
    1. FBM surface height          ← replaces current single Perlin (1 day)
    2. Biome blending              ← mountain vs flat vs ocean (1 day)
    3. Domain warping              ← organic shapes (2 hours, trivial add-on)
    4. Sea level + block layering  ← water, sand, snow by altitude (2 hours)
    5. 3D cave noise               ← requires density function refactor (1 day)
    6. Rivers                      ← hardest, skip until later