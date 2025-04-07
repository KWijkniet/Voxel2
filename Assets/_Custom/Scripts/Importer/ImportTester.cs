using UnityEngine;
using System.Collections.Generic;

namespace Custom.Importer
{
    [ExecuteInEditMode]
    public class ImportTester : MonoBehaviour
    {
        public bool test;

        public List<JSONData> voxels;
        public List<TextureData> textures;

        private ImportController importController;

        private void Start()
        {
            importController = new ImportController();
        }

        private void Update()
        {
            if (test)
            {
                test = false;
                if (importController == null) importController = ImportController.GetInstance();
                voxels = importController.voxels;
                textures = importController.textures;
                // Database.Instance.AddVoxel(importController.voxels[0]);
            }
        }
    }
}
