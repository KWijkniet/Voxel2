//using System.Collections.Generic;
//using Unity.Mathematics;
//using UnityEngine;

//namespace V3
//{
//    [RequireComponent(typeof(MeshFilter)), RequireComponent(typeof(MeshRenderer))]
//    public class Octree : MonoBehaviour
//    {
//        public float lodDistance = 40f;
//        public int worldSize = 32;

//        private OctreeNode root;
//        private float3 lastCameraPos = float3.zero;
//        private MeshFilter mf;
//        private Mesh mesh;

//        private void Start()
//        {
//            mf = GetComponent<MeshFilter>();

//            root = new OctreeNode(null, new int3(0,0,0), worldSize);

//            // Insert some voxels
//            root.Insert(new int3(0, 0, 0), 2);
//            root.Insert(new int3(1, 0, 0), 2);
//            root.Insert(new int3(0, 0, 1), 2);
//            root.Insert(new int3(1, 0, 1), 2);
//            //root.Insert(new int3(2, 2, 2), 2);
//            //root.Insert(new int3(5, 5, 5), 1);
//            //root.Insert(new int3(8, 12, 16), 1);
//            //root.Insert(new int3(20, 20, 20), 2);

//            mesh = new Mesh();
//            mf.mesh = mesh;
//        }

//        private void Update()
//        {
//            if (root == null) return;

//            float3 camPos = Camera.main.transform.position;
//            if (math.all(camPos == lastCameraPos)) return;

//            lastCameraPos = camPos;

//            // Update mesh (LOD)
//            root.UpdateLOD(camPos, lodDistance);
//        }

//        private void OnDrawGizmos()
//        {
//            if (root != null)
//            {
//                Gizmos.color = Color.yellow;
//                root.DrawGizmosRecursive();
//            }
//        }
//    }

//    public class OctreeNode
//    {
//        public bool isLeaf = true;
//        public byte voxel = 0;
//        public OctreeNode[] children = null;

//        public int3 pos;
//        public int size;
//        public int depth;

//        // Runtime LOD info
//        public bool ShouldRender;
//        private OctreeNode parent;

//        public OctreeNode(OctreeNode parent, int3 pos, int size, int depth = 0)
//        {
//            this.parent = parent;
//            this.pos = pos;
//            this.size = size;
//            this.depth = depth;
//        }

//        public void Subdivide()
//        {
//            if (!isLeaf) return;

//            int childSize = size / 2;
//            children = new OctreeNode[8];

//            for (int i = 0; i < 8; i++)
//            {
//                int3 offset = new int3(
//                    (i & 1) != 0 ? childSize : 0,
//                    (i & 2) != 0 ? childSize : 0,
//                    (i & 4) != 0 ? childSize : 0
//                );

//                children[i] = new OctreeNode(this, pos + offset, childSize, depth + 1);
//            }

//            isLeaf = false;
//        }

//        public void Insert(int3 point, byte type)
//        {
//            if (size <= 1)
//            {
//                voxel = type;
//                parent?.UpdateVoxelFromChildren();
//                return;
//            }

//            if (isLeaf)
//                Subdivide();

//            foreach (var child in children)
//            {
//                if (IsPointInCube(point, child.pos, child.size))
//                {
//                    child.Insert(point, type);
//                    break;
//                }
//            }

//            // Once a child was modified, update this node's voxel value
//            //UpdateVoxelFromChildren();
//        }

//        public void UpdateLOD(float3 camPos, float lodDistance)
//        {
//            float3 center = pos + new float3(size / 2f);
//            float distance = math.distance(center, camPos);

//            if (voxel == 0)
//            {
//                ShouldRender = false;
//                return;
//            }

//            float lodThreshold = lodDistance / math.pow(2, depth);

//            if (distance < lodThreshold && size > 1)
//            {
//                if (isLeaf)
//                    Subdivide();

//                ShouldRender = false;

//                foreach (var child in children)
//                {
//                    child.UpdateLOD(camPos, lodDistance);
//                }
//            }
//            else
//            {
//                // Collapse to this node (coarser level)
//                ShouldRender = true;
//            }
//        }

//        public void DrawGizmosRecursive()
//        {
//            Vector3 center = new Vector3(pos.x, pos.y, pos.z) + Vector3.one * (size / 2f);
//            Vector3 sizeVec = Vector3.one * size;

//            if (isLeaf && children == null && voxel != 0)
//            {
//                Gizmos.color = voxel == 1 ? Color.grey : voxel == 2 ? Color.green : Color.blue;
//                Gizmos.DrawCube(center, sizeVec);
//            }
            
//            if (!isLeaf && children != null)
//            {
//                Gizmos.color = new Color(0.3f, 0.5f, 1f, 0.2f);
//                Gizmos.DrawWireCube(center, sizeVec);

//                foreach (var child in children)
//                    child.DrawGizmosRecursive();
//            }
//        }

//        public byte ContainsSolidVoxels()
//        {
//            if (isLeaf)
//                return voxel;

//            foreach (var child in children)
//            {
//                byte childVoxel = child.ContainsSolidVoxels();
//                if (childVoxel != 0)
//                    return childVoxel;
//            }

//            return 0;
//        }

//        private bool IsPointInCube(int3 point, int3 cubePos, int cubeSize)
//        {
//            return point.x >= cubePos.x && point.x < cubePos.x + cubeSize &&
//                   point.y >= cubePos.y && point.y < cubePos.y + cubeSize &&
//                   point.z >= cubePos.z && point.z < cubePos.z + cubeSize;
//        }

//        private void UpdateVoxelFromChildren()
//        {
//            if (isLeaf || children == null) return;

//            Dictionary<byte, int> voxelCounts = new Dictionary<byte, int>();

//            int solidCount = 0;

//            foreach (var child in children)
//            {
//                if (child == null) continue;

//                byte v = child.voxel;
//                if (v != 0)
//                {
//                    solidCount++;

//                    if (!voxelCounts.ContainsKey(v))
//                        voxelCounts[v] = 0;

//                    voxelCounts[v]++;
//                }
//            }

//            // Check if a voxel type appears in ≥4 children
//            byte mostCommonVoxel = 0;
//            int maxCount = 0;

//            foreach (var kvp in voxelCounts)
//            {
//                if (kvp.Value > maxCount)
//                {
//                    maxCount = kvp.Value;
//                    mostCommonVoxel = kvp.Key;
//                }
//            }

//            // Use majority rule: at least half (4) of the children must match
//            voxel = (maxCount >= 4) ? mostCommonVoxel : (byte)0;

//            // Propagate update upward
//            parent?.UpdateVoxelFromChildren();
//        }
//    }
//}
