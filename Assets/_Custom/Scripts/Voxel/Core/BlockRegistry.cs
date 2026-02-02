using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Voxel.Core
{
    /// <summary>
    /// Registry of all block types in the game.
    /// ScriptableObject for easy editing in Unity Inspector.
    /// </summary>
    [CreateAssetMenu(fileName = "BlockRegistry", menuName = "Voxel/Block Registry")]
    public class BlockRegistry : ScriptableObject
    {
        [SerializeField]
        private List<BlockTypeData> blockTypes = new List<BlockTypeData>();

        private Dictionary<ushort, BlockType> blockLookup;

        [System.Serializable]
        public class BlockTypeData
        {
            public ushort id;
            public string name;
            public bool isSolid = true;
            public bool isTransparent = false;
            public Color color = Color.white;
        }

        public void Initialize()
        {
            blockLookup = new Dictionary<ushort, BlockType>();

            // Always add air at index 0
            blockLookup[Constants.BLOCK_AIR] = BlockType.CreateAir();

            // Add configured blocks
            foreach (var data in blockTypes)
            {
                if (data.id == Constants.BLOCK_AIR) continue; // Skip air, already added

                blockLookup[data.id] = new BlockType
                {
                    id = data.id,
                    name = data.name,
                    isSolid = data.isSolid,
                    isTransparent = data.isTransparent,
                    color = new float4(data.color.r, data.color.g, data.color.b, data.color.a)
                };
            }
        }

        public BlockType GetBlock(ushort id)
        {
            if (blockLookup == null) Initialize();

            if (blockLookup.TryGetValue(id, out var block))
            {
                return block;
            }

            // Return air for unknown blocks
            return blockLookup[Constants.BLOCK_AIR];
        }

        public bool IsSolid(ushort id)
        {
            return GetBlock(id).isSolid;
        }

        public bool IsTransparent(ushort id)
        {
            return GetBlock(id).isTransparent;
        }

        public float4 GetColor(ushort id)
        {
            return GetBlock(id).color;
        }

        /// <summary>
        /// Create default block registry with basic block types.
        /// Used when no ScriptableObject is assigned.
        /// </summary>
        public static BlockRegistry CreateDefault()
        {
            var registry = CreateInstance<BlockRegistry>();
            registry.blockTypes = new List<BlockTypeData>
            {
                new BlockTypeData { id = Constants.BLOCK_STONE, name = "Stone", isSolid = true, color = new Color(0.5f, 0.5f, 0.5f) },
                new BlockTypeData { id = Constants.BLOCK_DIRT, name = "Dirt", isSolid = true, color = new Color(0.55f, 0.35f, 0.2f) },
                new BlockTypeData { id = Constants.BLOCK_GRASS, name = "Grass", isSolid = true, color = new Color(0.3f, 0.7f, 0.2f) }
            };
            registry.Initialize();
            return registry;
        }
    }
}
