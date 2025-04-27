using UnityEngine;

public class Helpers
{
    public static int CoordinatesToIndex(int x, int y, int z, int width = 16, int height = 16, int depth = 16) {
        if (x < 0 || x >= width || y < 0 || y >= height || z < 0 || z >= depth)
            return -1;
        return x + y * width + z * width * height;
    }

    public static Vector3Int IndexToCoordinates(int index, int width, int height, int depth) {
        // return new Vector3Int(index / (size * size), (index / size) % size, index % size);
        int z = index % width; // X-coordinate
        int y = (index / width) % height; // Y-coordinate
        int x = index / (width * height); // Z-coordinate
        
        return new Vector3Int(x, y, z);
    }
}