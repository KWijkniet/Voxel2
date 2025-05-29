using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Unity.Mathematics;

namespace Custom.Voxels
{
    internal class ChunkManager
    {
        private Hashtable chunks = new Hashtable();

        public void Clear()
        {
            chunks.Clear();
        }

        public int Count()
        {
            return chunks.Count;
        }

        public Chunk[] GetAll()
        {
            return chunks.Values.Cast<Chunk>().ToArray();
        }

        public Chunk GetChunk(int x, int y, int z)
        {
            int3 key = new int3(x, y, z);
            return GetChunk(key);
        }

        public Chunk GetChunk(int3 key)
        {
            if (chunks.ContainsKey(key))
            {
                return (Chunk)chunks[key];
            }

            return null;
        }

        public void SetChunk(int x, int y, int z, Chunk chunk)
        {
            int3 key = new int3(x, y, z);
            SetChunk(key, chunk);
        }

        public void SetChunk(int3 key, Chunk chunk)
        {
            if (!chunks.ContainsKey(key))
            {
                chunks[key] = chunk;
            }
        }
    }
}
