using UnityEngine;

namespace Custom.Importer
{
    [System.Serializable]
    public class JSONData
    {
        public string id;
        public string type;
        public string displayName;
        public string folder;
        public AnimationFrame[] textures;
        public ModelData[] mesh;
        public float animationSpeed;
        public bool isLiquid;
        public bool isTransparent;
        public string sound;

        public int GetId(){
            return int.Parse(id);
        }
    }

    [System.Serializable]
    public class AnimationFrame
    {
        public AnimationValue up;
        public AnimationValue down;
        public AnimationValue north;
        public AnimationValue south;
        public AnimationValue east;
        public AnimationValue west;
    }

    [System.Serializable]
    public class ModelData
    {
        public float[] from;
        public float[] to;
        public string direction;
    }

    [System.Serializable]
    public class AnimationValue
    {
        public int index;
        public string path;

        public static implicit operator AnimationValue(string path)
        {
            return new AnimationValue { path = path, index = 0 };
        }
    }
}
