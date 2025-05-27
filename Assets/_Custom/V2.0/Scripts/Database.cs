using Custom.Importer;
using Unity.Collections;

namespace Custom.Voxels
{
    internal class Database
    {
        public static Database Instance;
        private  NativeList<VoxelEntry> entries;

        public Database()
        {
            Instance = this;
            entries = new(Allocator.Persistent);
            ImportController importController = ImportController.GetInstance();
            importController.Import();

            foreach (JSONData voxel in importController.voxels)
            {
                entries.Add(new VoxelEntry
                {
                    id = (byte)voxel.GetId(),
                    type = VoxelType.Voxel,
                    name = new FixedString64Bytes(voxel.displayName),
                    canGreedyMesh = (byte) (voxel.canGreedyMesh ? 1 : 0),
                    isTransparent = (byte) (voxel.isTransparent ? 1 : 0),
                });
            }
        }

        public static VoxelEntry? GetEntry(byte id)
        {
            foreach (VoxelEntry item in Instance.entries)
            {
                if(item.id == id)
                {
                    return item;
                }
            }

            return null;
        }
    }

    public enum VoxelType
    {
        Voxel = 0,
        Detail = 1,
    }

    public struct VoxelEntry
    {
        public byte id;
        public VoxelType type;
        public FixedString64Bytes name;

        public byte canGreedyMesh;
        public byte isTransparent;
    }
}
