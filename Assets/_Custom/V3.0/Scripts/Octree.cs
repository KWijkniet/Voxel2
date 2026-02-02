using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.Mathematics;
using Unity.VisualScripting;
using UnityEngine;

namespace V3
{
    [RequireComponent(typeof(MeshFilter)), RequireComponent(typeof(MeshRenderer))]
    public class Octree : MonoBehaviour
    {
        public int lod = 0;
        public int rootSize = 32;
        public int maxDepth = 1;

        [Header("Debugging")]
        public bool showVoxels;
        public bool showOctree;
        public static int instanceCount = 0;

        private OctreeNode root;
        //private float3 lastCameraPos = float3.zero;
        //private MeshFilter mf;
        //private Mesh mesh;

        //private int lastLod = -1;

        private void Start()
        {
            //mf = GetComponent<MeshFilter>();

            //PaletteGenerator.GeneratePalette4096();
            //PaletteGenerator.GeneratePalette(6);

            root = new OctreeNode(new int3(0,0,0), rootSize, maxDepth);
            StartCoroutine(addVoxels());

            //mesh = new Mesh();
            //mf.mesh = mesh;
        }

        private IEnumerator addVoxels()
        {
            for (int x = 0; x < 16; x++)
            {
                for (int y = 0; y < 16; y++)
                {
                    for (int z = 0; z < 16; z++)
                    {
                        SetVoxel(x, y, z);
                        yield return new WaitForSeconds(0.05f);
                    }
                }
            }
            Debug.Log("Ended with: " + instanceCount.ToString() + " instances");
        }

        //private void Update()
        //{
        //    if (root == null) return;
            
        //    // Calculate lod
        //    // ...

        //    //if (lod == lastLod) return;
        //    //lastLod = lod;

        //    //mesh.Clear();

        //    //List<Vector3> vertices = new List<Vector3>();
        //    //List<int> triangles = new List<int>();
        //    //List<Color32> colors = new List<Color32>();

        //    //root.BuildMesh(vertices, triangles, colors, lod);

        //    //mesh.SetVertices(vertices);
        //    //mesh.SetTriangles(triangles, 0);
        //    //mesh.SetColors(colors);
        //    //mesh.RecalculateNormals();
        //}

        private void OnDrawGizmos()
        {
            if (!showVoxels && !showOctree) return;

            if (root != null)
            {
                root.DrawGizmos(lod, showVoxels, showOctree);
            }
        }

        private void SetVoxel(int _x, int _y, int _z)
        {
            float pixel = 1f / 16f;
            float startX = 0;
            float startY = 0;
            float startZ = 0;

            // Indices into CustomPalette.palette[] (defined based on your 32-color palette)
            byte[] grassIndices = new byte[] { 8, 9, 10, 11 };       // greens
            byte[] dirtIndices = new byte[] { 1, 2, 3, 4, 5 };       // browns

            for (int x = 0; x < 16; x++)
            {
                for (int z = 0; z < 16; z++)
                {
                    for (int y = 0; y < 16; y++)
                    {
                        float3 pos = new float3(
                            startX + (x + _x * 16f) * pixel,
                            startY + (y + _y * 16f) * pixel,
                            startZ + (z + _z * 16f) * pixel
                        );

                        byte colorIndex;
                        if (y >= 14) // Top layer: Grass
                        {
                            colorIndex = grassIndices[UnityEngine.Random.Range(0, grassIndices.Length)];
                        }
                        else // Below: Dirt
                        {
                            colorIndex = dirtIndices[UnityEngine.Random.Range(0, dirtIndices.Length)];
                        }

                        root.Insert(pos, 1, colorIndex);
                    }
                }
            }
        }
    }

    public class OctreeNode
    {
        private float3 pos;
        private float size;
        private int depth;
        private OctreeNode parent;

        private byte colorIndex;
        private byte voxel = 0;
        private OctreeNode[] children = null;

        public OctreeNode(float3 pos, float size, int depth = 0, OctreeNode parent = null)
        {
            this.pos = pos;
            this.size = size;
            this.depth = depth;
            this.parent = parent;
        }

        public void Insert(float3 point, byte type, byte color)
        {
            if (depth == 4) voxel = type;

            if (depth == 0)
            {
                colorIndex = color;
                parent?.cacheAverage();
                return;
            }

            if (children == null) Subdivide();

            foreach (var child in children)
            {
                if (IsPointInside(point, child.pos, child.size))
                {
                    child.Insert(point, type, color);
                    break;
                }
            }
        }

        public void Subdivide()
        {
            float childSize = size / 2f;

            children = new OctreeNode[8];
            for (int i = 0; i < 8; i++)
            {
                float3 offset = new float3(
                    (i & 1) != 0 ? childSize : 0,
                    (i & 2) != 0 ? childSize : 0,
                    (i & 4) != 0 ? childSize : 0
                );

                children[i] = new OctreeNode(pos + offset, childSize, depth - 1, this);
                Octree.instanceCount++;
            }
        }

        public void DrawGizmos(int drawDepth, bool showVoxels, bool showOctree)
        {
            if (drawDepth == depth || children == null)
            {
                Vector3 center = new Vector3(pos.x, pos.y, pos.z) + Vector3.one * (size / 2f);
                Vector3 sizeVec = Vector3.one * size;

                if (showOctree && (showVoxels && voxel == 0) || !showVoxels)
                {
                    Gizmos.color = Color.blue;
                    Gizmos.DrawWireCube(center, sizeVec);
                }

                if (colorIndex != 0 && showVoxels)
                {
                    Gizmos.color = PaletteGenerator.palette[colorIndex];
                    Gizmos.DrawCube(center, sizeVec);
                }
                return;
            }

            if (children != null)
            {
                foreach (var child in children)
                {
                    child.DrawGizmos(drawDepth, showVoxels, showOctree);
                }
            }
        }

        public void BuildMesh(List<Vector3> vertices, List<int> triangles, List<Color32> colors, int targetDepth)
        {
            if ((depth == targetDepth || children == null) && voxel != 0)
            {
                Vector3 basePos = new Vector3(pos.x, pos.y, pos.z);
                float s = size;

                Color32 color = PaletteGenerator.palette[colorIndex];

                // 8 vertices of the cube
                Vector3[] v = new Vector3[8]
                {
                    basePos + new Vector3(0, 0, 0),
                    basePos + new Vector3(s, 0, 0),
                    basePos + new Vector3(s, s, 0),
                    basePos + new Vector3(0, s, 0),
                    basePos + new Vector3(0, 0, s),
                    basePos + new Vector3(s, 0, s),
                    basePos + new Vector3(s, s, s),
                    basePos + new Vector3(0, s, s)
                };

                int index = vertices.Count;

                // Add only visible faces (we're adding all for simplicity)
                int[] faceTris = {
                    0, 2, 1, 0, 3, 2, // Front
                    5, 6, 4, 6, 7, 4, // Back
                    4, 7, 0, 7, 3, 0, // Left
                    1, 2, 5, 2, 6, 5, // Right
                    3, 7, 2, 7, 6, 2, // Top
                    4, 0, 5, 0, 1, 5  // Bottom
                };

                for (int i = 0; i < 8; i++)
                {
                    vertices.Add(v[i]);
                    colors.Add(color);
                }

                for (int i = 0; i < faceTris.Length; i++)
                {
                    triangles.Add(index + faceTris[i]);
                }

                return;
            }

            if (children != null)
            {
                foreach (var child in children)
                {
                    child.BuildMesh(vertices, triangles, colors, targetDepth);
                }
            }
        }

        private void cacheAverage()
        {
            if (children == null || children.Length == 0)
                return;

            Dictionary<byte, int> types = new Dictionary<byte, int>();
            int total = 0;

            int sumR = 0, sumG = 0, sumB = 0;

            foreach (var child in children)
            {
                byte type = child.voxel;
                byte childColorIndex = child.colorIndex;

                if (types.ContainsKey(type)) types[type]++;
                else types[type] = 1;

                Color32 color = PaletteGenerator.palette[childColorIndex];
                sumR += color.r;
                sumG += color.g;
                sumB += color.b;
                total++;
            }

            // Set majority voxel type
            voxel = types.Aggregate((x, y) => x.Value > y.Value ? x : y).Key;

            // Compute average color and find nearest palette entry
            byte avgR = (byte)(sumR / total);
            byte avgG = (byte)(sumG / total);
            byte avgB = (byte)(sumB / total);

            //colorIndex = PaletteGenerator.FindClosestPaletteColorIndex(avgR, avgG, avgB);
            colorIndex = PaletteGenerator.FindClosestCustomPaletteIndex(avgR, avgG, avgB);

            parent?.cacheAverage();
        }

        private bool IsPointInside(float3 point, float3 cubePos, float cubeSize)
        {
            return point.x >= cubePos.x && point.x < cubePos.x + cubeSize &&
                   point.y >= cubePos.y && point.y < cubePos.y + cubeSize &&
                   point.z >= cubePos.z && point.z < cubePos.z + cubeSize;
        }
    }

    public static class PaletteGenerator
    {
        public static readonly Color32[] palette = new Color32[]
        {
            new Color32(0, 0, 0, 255),
            new Color32(34, 32, 52, 255),
            new Color32(69, 40, 60, 255),
            new Color32(102, 57, 49, 255),
            new Color32(143, 86, 59, 255),
            new Color32(223, 113, 38, 255),
            new Color32(217, 160, 102, 255),
            new Color32(238, 195, 154, 255),

            new Color32(251, 242, 54, 255),
            new Color32(153, 229, 80, 255),
            new Color32(106, 190, 48, 255),
            new Color32(55, 148, 110, 255),
            new Color32(75, 105, 47, 255),
            new Color32(82, 75, 36, 255),
            new Color32(50, 60, 57, 255),
            new Color32(63, 63, 116, 255),

            new Color32(40, 96, 130, 255),
            new Color32(91, 110, 255, 255),
            new Color32(99, 155, 255, 255),
            new Color32(95, 205, 228, 255),
            new Color32(203, 219, 252, 255),
            new Color32(255, 255, 255, 255),
            new Color32(155, 173, 183, 255),
            new Color32(132, 126, 135, 255),

            new Color32(105, 106, 106, 255),
            new Color32(89, 86, 82, 255),
            new Color32(118, 66, 138, 255),
            new Color32(172, 50, 50, 255),
            new Color32(217, 87, 99, 255),
            new Color32(215, 123, 186, 255),
            new Color32(143, 151, 74, 255),
            new Color32(138, 111, 48, 255)
        };

        public static byte FindClosestCustomPaletteIndex(byte r, byte g, byte b)
        {
            int bestDistance = int.MaxValue;
            byte bestIndex = 0;

            for (byte i = 0; i < palette.Length; i++)
            {
                Color32 color = palette[i];
                int dr = color.r - r;
                int dg = color.g - g;
                int db = color.b - b;
                int dist = dr * dr + dg * dg + db * db;

                if (dist < bestDistance)
                {
                    bestDistance = dist;
                    bestIndex = i;
                    if (dist == 0) break; // exact match
                }
            }

            return bestIndex;
        }

        //// Backup
        //public static void GeneratePalette4096()
        //{
        //    palette = new Color32[4096];
        //    int index = 0;

        //    // 16 levels for R, G, B = 16 x 16 x 16 = 4096
        //    for (int r = 0; r < 16; r++)
        //        for (int g = 0; g < 16; g++)
        //            for (int b = 0; b < 16; b++)
        //            {
        //                byte red = (byte)(r * 17); // 17 * 15 = 255
        //                byte green = (byte)(g * 17);
        //                byte blue = (byte)(b * 17);
        //                palette[index++] = new Color32(red, green, blue, 255);
        //            }
        //}
    }
}