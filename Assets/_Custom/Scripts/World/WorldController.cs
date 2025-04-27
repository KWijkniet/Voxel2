using UnityEngine;

public class WorldController : MonoBehaviour
{
    public int width = 16, height = 16, depth = 16;
    public int waterLevel = 0;
    public int renderDistance = 16;
    public List<Material> materials;

    private List<RenderParams> renderParams;

    private void Awake()
    {
        Database.worldController = this;
        Database.Import();
    }

    private void Update()
    {

    }
}
