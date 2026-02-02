# Large-Scale Voxel Engine

A Unity-based voxel engine capable of rendering worlds 10x+ larger than Minecraft, with full editing support.

## Target Specifications

| Spec | Value |
|------|-------|
| Voxel density | 16 voxels per Unity unit |
| World scale | 10x Minecraft (massive render distances) |
| Block types | 256+ unique types (16-bit storage) |
| Terrain | Hybrid heightmap + 3D caves |
| Editing | Full dig/place with undo/redo |
| Persistence | Delta-only (save changes, regenerate base from seed) |
| Platform | PC, 60+ FPS |

---

## Implementation Phases

### Phase 1: Foundation (32x32x32 Prototype)

**Goal:** Get something rendering on screen

#### Step 1.1: Core Data Structures

Create `Assets/_Custom/Scripts/Voxel/Core/`:

**VoxelData.cs**
```csharp
// Single voxel representation
public struct Voxel
{
    public ushort typeId; // 0 = air, 1+ = solid block types
}
```

**ChunkData.cs**
```csharp
// 16x16x16 voxel storage using NativeArray for Burst compatibility
// Index formula: x + y * 16 + z * 256
// Memory: 16^3 * 2 bytes = 8KB per chunk
```

**BlockRegistry.cs**
```csharp
// Registry of block types with properties (solid, transparent, etc.)
// ScriptableObject for easy editing in Unity
```

#### Step 1.2: Coordinate System

**Constants:**
- `CHUNK_SIZE = 16` (voxels per axis)
- `VOXEL_SCALE = 0.0625f` (1/16 Unity units per voxel)

**Conversions:**
- World position → Chunk coordinate: `floor(worldPos / CHUNK_SIZE)`
- World position → Local voxel: `worldPos % CHUNK_SIZE`
- Index → XYZ: `x = i % 16, y = (i / 16) % 16, z = i / 256`

#### Step 1.3: Simple Terrain Generator

**TerrainGenerator.cs**
- Use Unity.Mathematics Perlin noise
- Heightmap generation: `height = baseHeight + noise * amplitude`
- Fill solid below height, air above
- Burst-compiled job for performance

#### Step 1.4: Naive Mesh Generation (First Pass)

**NaiveMesher.cs**
- Generate one quad per visible voxel face
- Check 6 neighbors to determine visibility
- Output: vertices, triangles, UVs
- No optimization yet - establish correctness first

#### Step 1.5: Basic Rendering

**VoxelWorld.cs** (MonoBehaviour)
- Generate 2x2x2 chunks (32x32x32 voxels total)
- Create meshes and apply single material
- Verify rendering works

**Deliverable:** Visible 32x32x32 terrain block

---

### Phase 2: Performance Foundation

#### Step 2.1: Greedy Meshing

**GreedyMesher.cs**
- Merge adjacent faces of same type into larger quads
- Reduces polygon count by ~80%
- Process each face direction independently
- Burst-compiled for speed

**Reference:** [0fps Meshing Article](https://0fps.net/2012/06/30/meshing-in-a-minecraft-game/)

#### Step 2.2: Job System Integration

**ChunkGenerationJob.cs**
- IJob for terrain generation
- Burst compile attribute
- NativeArray for all data (no managed allocations)

**ChunkMeshingJob.cs**
- IJob for mesh generation
- Takes voxel data + neighbor data as input
- Outputs mesh data arrays

#### Step 2.3: Chunk Manager

**ChunkManager.cs**
- Dictionary<int3, Chunk> for spatial lookup
- Queue-based loading/unloading
- Priority based on distance to player
- Batch processing (N chunks per frame)

#### Step 2.4: Frustum Culling

- Calculate camera frustum planes
- Test chunk bounds against planes
- Skip mesh rendering for off-screen chunks

**Deliverable:** Smooth rendering of 8x8x8 chunks (128x128x128 voxels)

---

### Phase 3: Scaling Up

#### Step 3.1: LOD System

**LODMesher.cs**
- Downsample voxels by 2x, 4x, 8x, 16x factors
- Majority voting for block type selection
- Distance-based LOD level selection

**LOD Levels:**
| Level | Scale | Distance (chunks) |
|-------|-------|-------------------|
| 0 | 1x | 0-2 |
| 1 | 2x | 2-8 |
| 2 | 4x | 8-24 |
| 3 | 8x | 24-64 |
| 4 | 16x | 64+ |

#### Step 3.2: Streaming System

**ChunkStreamer.cs**
- Calculate required chunks from player position
- Load new chunks entering range
- Unload chunks leaving range
- Async chunk generation on worker threads

#### Step 3.3: Memory Optimization - Palette Compression

**PaletteChunk.cs**
- Local palette: maps index → global block ID
- Voxel array stores palette indices (4-8 bits instead of 16)
- Reduces memory 2-4x for typical chunks

**Memory Budget:**
- 10,000 chunks loaded
- ~1.5KB average per chunk (compressed)
- Total: ~15MB voxel data

#### Step 3.4: Draw Call Batching

**BatchedRenderer.cs**
- Group chunks by material/LOD
- Use Graphics.RenderMeshInstanced
- Target: <100 draw calls for 10,000 chunks

**Deliverable:** Render distance 160+ chunks at 60 FPS

---

### Phase 4: Cave & Terrain Enhancement

#### Step 4.1: 3D Noise for Caves

**CaveGenerator.cs**
- 3D Perlin/Simplex noise
- Multiple octaves for varied cave sizes
- Combine with heightmap: `solid = belowHeight && density3D > threshold`

#### Step 4.2: Biome System

**BiomeProvider.cs**
- Temperature/humidity noise for biome selection
- Per-biome terrain parameters
- Smooth biome transitions

#### Step 4.3: Structure Generation

- Tree placement using deterministic random
- Cave features (stalactites, ore deposits)
- Future: buildings, dungeons

**Deliverable:** Rich terrain with caves and biomes

---

### Phase 5: Editing System

#### Step 5.1: Voxel Modification

**EditManager.cs**
- SetVoxel(worldPos, blockType)
- Mark affected chunks as dirty
- Trigger re-meshing

#### Step 5.2: Dirty Tracking & Incremental Remesh

**DirtyTracker.cs**
- Track modified chunks
- Schedule remesh with priority
- Handle cross-chunk boundary edits (mark neighbors dirty too)

#### Step 5.3: Delta Storage

**DeltaStorage.cs**
- Track changes from procedural generation
- Format: `List<(position, newBlockId)>` per chunk
- Only edited voxels stored, not entire chunks

#### Step 5.4: Undo/Redo

**UndoStack.cs**
- Record inverse operations
- Group related edits
- Memory limit (discard old history)

**Deliverable:** Full voxel editing with undo

---

### Phase 6: Persistence

#### Step 6.1: Region File Format

**RegionFile.cs**
- 16x16 chunks per region file
- Header with chunk offset table
- Delta-only storage (not full chunks)

**File Format (.vxr):**
```
Header (4KB): magic, version, seed, chunk offsets
Chunk deltas: compressed edit lists
```

#### Step 6.2: Save/Load System

**SaveManager.cs**
- Async file I/O
- LZ4 compression for deltas
- Auto-save on chunk unload

#### Step 6.3: World Regeneration

- Regenerate base terrain from seed
- Apply stored deltas
- Corruption recovery (regenerate if delta invalid)

**Deliverable:** Persistent world with tiny save files

---

## Project Structure

```
Assets/_Custom/Scripts/Voxel/
├── Core/
│   ├── Constants.cs          # CHUNK_SIZE, VOXEL_SCALE
│   ├── VoxelData.cs          # Voxel struct
│   ├── ChunkData.cs          # Raw chunk storage
│   ├── ChunkCoord.cs         # Coordinate helpers
│   └── BlockRegistry.cs      # Block type definitions
├── World/
│   ├── VoxelWorld.cs         # Main MonoBehaviour
│   ├── Chunk.cs              # Chunk state + mesh
│   ├── ChunkManager.cs       # Lifecycle management
│   └── ChunkStreamer.cs      # Load/unload logic
├── Generation/
│   ├── TerrainGenerator.cs   # Heightmap + caves
│   ├── CaveGenerator.cs      # 3D noise caves
│   ├── BiomeProvider.cs      # Biome selection
│   └── StructureGen.cs       # Trees, features
├── Meshing/
│   ├── NaiveMesher.cs        # Simple cube faces
│   ├── GreedyMesher.cs       # Optimized meshing
│   └── LODMesher.cs          # Downsampled meshes
├── Jobs/
│   ├── GenerateChunkJob.cs   # Burst terrain job
│   └── MeshChunkJob.cs       # Burst meshing job
├── Rendering/
│   ├── ChunkRenderer.cs      # Per-chunk rendering
│   ├── BatchedRenderer.cs    # Instanced rendering
│   └── FrustumCuller.cs      # Visibility testing
├── Editing/
│   ├── EditManager.cs        # Voxel modification
│   ├── DirtyTracker.cs       # Change tracking
│   ├── DeltaStorage.cs       # Edit storage
│   └── UndoStack.cs          # Undo/redo
└── Persistence/
    ├── SaveManager.cs        # Save coordination
    ├── RegionFile.cs         # File format
    └── ChunkSerializer.cs    # Delta serialization
```

---

## Key Algorithms & References

### Greedy Meshing
- **Source:** [0fps Blog - Meshing in Minecraft](https://0fps.net/2012/06/30/meshing-in-a-minecraft-game/)
- Merges adjacent faces into larger quads
- Reduces vertices by 80%+

### Palette Compression
- **Source:** [Minecraft Wiki - Chunk Format](https://minecraft.wiki/w/Chunk_format)
- Local palette per chunk
- Variable bit-width indices

### 3D Terrain Density
- **Source:** Sebastian Lague's procedural tutorials
- Combine heightmap with 3D noise for caves

### Region Files
- **Source:** [Minecraft Region Format](https://minecraft.wiki/w/Region_file_format)
- Fixed-size sectors for random access

---

## Verification Checklist

### Phase 1
- [ ] 32x32x32 terrain renders correctly
- [ ] Mesh has correct normals (lighting works)
- [ ] Performance: <1ms for 8 chunks

### Phase 2
- [ ] Greedy mesh reduces vertices by 70%+
- [ ] Frustum culling works (check profiler)
- [ ] 64 chunks at 60 FPS

### Phase 3
- [ ] Streaming works while walking
- [ ] LOD transitions smooth (no popping)
- [ ] Memory: <100MB for voxel data

### Phase 4
- [ ] Caves are explorable underground
- [ ] Caves connect properly
- [ ] Biome transitions smooth

### Phase 5
- [ ] Place/remove blocks works
- [ ] Undo/redo works
- [ ] Cross-chunk edits work

### Phase 6
- [ ] Save persists edits
- [ ] Reload restores edits
- [ ] Delete save + regenerate matches original terrain

---

## Memory Estimates

| Component | Estimate |
|-----------|----------|
| 10,000 chunks (palette compressed) | ~15 MB |
| Meshes (greedy + LOD) | ~100 MB |
| Delta storage (100 hours play) | ~1.6 MB |
| Typical save file | 1-10 MB |

---

## Implementation Order

1. Core data structures (ChunkData, BlockRegistry)
2. Simple terrain generation (heightmap only)
3. Naive meshing (establish correctness)
4. Greedy meshing (optimize geometry)
5. Job system (multi-threaded generation)
6. Chunk manager (streaming)
7. LOD system (distance-based quality)
8. Cave generation (3D noise)
9. Editing system (place/remove)
10. Delta storage (track changes)
11. Persistence (save/load)

**Start with Phase 1 to get a working 32x32x32 prototype, then iterate.**
