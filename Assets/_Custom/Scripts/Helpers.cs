using UnityEngine;

public class Helpers
{
    public static int CoordinatesToIndex(int x, int y, int z, int size = 16) {
         if (x < 0 || x >= size || y < 0 || y >= size || z < 0 || z >= size)
            return -1;
        return x + y * size + z * size * size;
    }

    public static Vector3Int IndexToCoordinates(int index, int size = 16) {
        return new Vector3Int(index / (size * size), (index / size) % size, index % size);
    }
}
