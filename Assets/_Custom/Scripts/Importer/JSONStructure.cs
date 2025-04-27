using UnityEngine;

namespace Custom.Importer
{
    [System.Serializable]
    public class JSONStructure
    {
        public string id;
        public string name;
        public StructureVariant[] variants;

        public int GetId(){
            return int.Parse(id);
        }

        public StructureVariant GetVariant(int id)
        {
            foreach (StructureVariant variant in this.variants)
            {
                if (int.Parse(variant.id) == id)
                {
                    return variant;
                }
            }
            return null;
        }
    }

    [System.Serializable]
    public class StructureVariant
    {
        public string id;
        public string name;
        public int width;
        public int height;
        public int depth;
        public int[] center;
        public int[] voxels;
    }
}
