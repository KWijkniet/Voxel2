using UnityEngine;

namespace Custom.Importer
{
    [System.Serializable]
    public class TextureData
    {
        public int index;
        public string path;
        public Texture2D texture;

        public TextureData(int index,Texture2D texture, string path)
        {
            this.index = index;
            this.texture = texture;
            this.path = path;
        }
    }
}
