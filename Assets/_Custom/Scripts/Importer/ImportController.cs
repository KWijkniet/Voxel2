using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json;
using System.IO;
using System.Linq;

namespace Custom.Importer
{
    public class ImportController
    {
        public List<JSONData> voxels;
        public List<TextureData> textures;
        public string targetDirName;
        public Texture2DArray texture2DArray;

        public ComputeBuffer voxelBuffer;
        private static ImportController instance;

        public static ImportController GetInstance(string targetDirName = null)
        {
            if (instance == null)
            {
                instance = new ImportController(targetDirName);
            }
            return instance;
        }

        public ImportController(string targetDirName = null)
        {
            this.targetDirName = targetDirName;
            Import();
        }

        public void Import()
        {
            if (voxelBuffer != null) voxelBuffer.Release();

            voxels = new List<JSONData>();
            textures = new List<TextureData>();

            ImportInternal();
            // Overwrite import with external if given 

            // Generate Texture2DArray
            GenerateTexture2DArray();

            //then Save To Disk as PNG
            SaveTexture2DArrayToFile();

            CreateComputeBuffer();
        }

        private void ImportInternal()
        {
            TextAsset[] jsonFiles = Resources.LoadAll<TextAsset>("Voxels");
            foreach (TextAsset jsonFile in jsonFiles)
            {
                voxels.Add(JsonConvert.DeserializeObject<JSONData>(jsonFile.text));
            }
            voxels = voxels.OrderBy(voxel => voxel.id).ToList();
            ImportInternalTextures();

            foreach (JSONData voxel in voxels)
            {
                Debug.Log(voxel.type + " " + voxel.displayName);
            }
        }

        private void ImportInternalTextures()
        {
            foreach (JSONData voxel in voxels)
            {
                string path = "Data/" + voxel.folder + "/textures/";
                if (voxel.type == "VOID") continue;

                foreach (AnimationFrame texture in voxel.textures)
                {
                    texture.up.index = TryAddTexture(Resources.Load<Texture2D>(System.IO.Path.ChangeExtension(path + texture.up.path, null)), path + texture.up.path);
                    texture.down.index = TryAddTexture(Resources.Load<Texture2D>(System.IO.Path.ChangeExtension(path + texture.down.path, null)), path + texture.down.path);
                    texture.north.index = TryAddTexture(Resources.Load<Texture2D>(System.IO.Path.ChangeExtension(path + texture.north.path, null)), path + texture.north.path);
                    texture.south.index = TryAddTexture(Resources.Load<Texture2D>(System.IO.Path.ChangeExtension(path + texture.south.path, null)), path + texture.south.path);
                    texture.east.index = TryAddTexture(Resources.Load<Texture2D>(System.IO.Path.ChangeExtension(path + texture.east.path, null)), path + texture.east.path);
                    texture.west.index = TryAddTexture(Resources.Load<Texture2D>(System.IO.Path.ChangeExtension(path + texture.west.path, null)), path + texture.west.path);
                }
            }
        }

        private int TryAddTexture(Texture2D texture, string path)
        {
            foreach (TextureData textureData in textures)
            {
                if (textureData.path == path)
                {
                    return textureData.index;
                }
            }

            //then Save To Disk as PNG
            byte[] bytes = texture.EncodeToPNG();
            var dirPath = Application.dataPath + "/SaveImages/";
            if(!Directory.Exists(dirPath)) {
                Directory.CreateDirectory(dirPath);
            }
            File.WriteAllBytes(dirPath + "Image" + textures.Count + ".png", bytes);

            TextureData newTexture = new TextureData(textures.Count, texture, path);
            textures.Add(newTexture);
            return newTexture.index;
        }

        private void GenerateTexture2DArray()
        {
            texture2DArray = new Texture2DArray(textures[0].texture.width, textures[0].texture.height, textures.Count, TextureFormat.ARGB32, false);
            texture2DArray.filterMode = FilterMode.Point;

            for (int i = 0; i < textures.Count; i++)
            {
                texture2DArray.SetPixels(textures[i].texture.GetPixels(), i);
            }
            texture2DArray.Apply();
        }

        private void SaveTexture2DArrayToFile()
        {
            if (texture2DArray == null)
            {
                Debug.LogError("Texture2DArray is null, cannot save to file");
                return;
            }

            // Create directory if it doesn't exist
            string directoryPath = Application.dataPath + "/SaveImages/";
            if (!Directory.Exists(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            // Save the Texture2DArray as an asset
            string filePath = directoryPath + "TextureArray.asset";
            
            // Save to file using Unity's asset serialization
            #if UNITY_EDITOR
            // Create a new asset file
            string relativePath = "Assets/SaveImages/TextureArray.asset";
            UnityEditor.AssetDatabase.CreateAsset(texture2DArray, relativePath);
            UnityEditor.AssetDatabase.SaveAssets();
            UnityEditor.AssetDatabase.Refresh();
            Debug.Log("Texture2DArray saved to: " + relativePath);
            #else
            Debug.LogWarning("Texture2DArray can only be saved in the Unity Editor");
            #endif
        }
    
        private void CreateComputeBuffer()
        {
            CBVoxel[] cbData = new CBVoxel[voxels.Count];

            for (int i = 0; i < voxels.Count; i++)
            {
                JSONData voxel = voxels[i];
                if (voxel != null && voxel.textures != null)
                {
                    cbData[i].frameCount = voxel.textures.Length;
                    cbData[i].animationSpeed = voxel.animationSpeed;
                    cbData[i].isLiquid = voxel.isLiquid ? 1 : 0;
                    unsafe
                    {
                        fixed (int* dirPtr = cbData[i].directions)
                        {
                            for (int frame = 0; frame < voxel.textures.Length && frame < 8; frame++)
                            {
                                int baseIndex = frame * 6;
                                dirPtr[baseIndex + 0] = voxel.textures[frame].up.index;
                                dirPtr[baseIndex + 1] = voxel.textures[frame].down.index;
                                dirPtr[baseIndex + 2] = voxel.textures[frame].north.index;
                                dirPtr[baseIndex + 3] = voxel.textures[frame].south.index;
                                dirPtr[baseIndex + 4] = voxel.textures[frame].east.index;
                                dirPtr[baseIndex + 5] = voxel.textures[frame].west.index;

                                // Debug output for first voxel
                                if (i == 4)
                                {
                                    Debug.Log($"Voxel 4, Frame {frame} - Up: {dirPtr[baseIndex + 0]}, Down: {dirPtr[baseIndex + 1]}, " +
                                    $"North: {dirPtr[baseIndex + 2]}, South: {dirPtr[baseIndex + 3]}, " +
                                    $"East: {dirPtr[baseIndex + 4]}, West: {dirPtr[baseIndex + 5]}");
                                }
                            }
                        }
                    }
                }
            }

            // Create and set the buffer
            int stride = sizeof(int) * (1 + 6 * 8) + sizeof(int) + sizeof(float); // Size of CBVoxel struct (frameCount + directions)
            voxelBuffer = new ComputeBuffer(voxels.Count, stride);
            voxelBuffer.SetData(cbData);

            // Debug: Verify buffer data
            CBVoxel[] verifyData = new CBVoxel[voxels.Count];
            voxelBuffer.GetData(verifyData);
            unsafe
            {
                fixed (int* dirPtr = verifyData[4].directions)
                {
                    Debug.Log($"Verify Voxel 4 - FrameCount: {verifyData[4].frameCount}");
                    for (int i = 0; i < 6; i++)
                    {
                        Debug.Log($"Direction {i}: {dirPtr[i]}");
                    }
                }
            }
        }
    }

    [System.Serializable]
    unsafe struct CBVoxel
    {
        public int frameCount; // Number of animation frames for this voxel
        public float animationSpeed; // Number of animation frames for this voxel
        public int isLiquid;
        public fixed int directions[6 * 8]; // 6 directions, 8 frames max
    }

    [System.Serializable]
    unsafe struct CBFrame
    {
        public fixed int directions[6]; // 6 directions per frame
    }

    public enum ImportTarget
    {
        INTERNAL,
        EXTERNAL
    }
}
