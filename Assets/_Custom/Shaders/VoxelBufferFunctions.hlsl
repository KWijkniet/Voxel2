#ifndef VOXEL_BUFFER_FUNCTIONS_INCLUDED
#define VOXEL_BUFFER_FUNCTIONS_INCLUDED

//REMOVE IF BROKEN
// Ensure proper alignment for GPU buffer
// #pragma pack_matrix(4)

struct CBVoxel
{
    int frameCount;           // offset: 0
    float animationSpeed;     // offset: 4
    int isLiquid;             // offset: 8
    int directions[48];       // offset: 16 (after frameCount and animationSpeed)
};

StructuredBuffer<CBVoxel> _VoxelBuffer;

void GetVoxelTexture_float(float3 voxelID_direction_animKey, out float index)
{
    uint voxelID = (uint)voxelID_direction_animKey.x;
    uint direction = min((uint)voxelID_direction_animKey.y, 5);
    uint animationKey = (uint)voxelID_direction_animKey.z;
    
    // Get the actual number of frames for this voxel
    int frameCount = _VoxelBuffer[voxelID].frameCount;
    
    // Simple frame selection - just wrap around if we exceed frame count
    if (frameCount > 0)
    {
        animationKey = animationKey % frameCount;
    }
    else
    {
        animationKey = 0;
    }
    
    // Calculate array index for the texture
    uint arrayIndex = (animationKey * 6) + direction;
    
    // Ensure array index is within bounds
    arrayIndex = min(arrayIndex, 47);
    
    // Get the texture index directly
    index = _VoxelBuffer[voxelID].directions[arrayIndex];
}

void GetAnimationSpeed_float(float1 voxelID, float1 time, out float animationSpeed)
{
    float multiplier = _VoxelBuffer[(uint)voxelID].animationSpeed;
    // animationSpeed = floor(time * multiplier + 0.0001);
    animationSpeed = time;
}

void IsLiquid_float(float1 voxelID, out bool isLiquid)
{
    isLiquid = _VoxelBuffer[(uint)voxelID].isLiquid == 1;
}

#endif