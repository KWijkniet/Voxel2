/// <summary>
/// Shared mutable flags passed by reference to all subsystems that need to
/// read or write DrawListDirty or NeedsMoreRequests.
/// All access is on the main thread only.
/// </summary>
internal sealed class WorldFlags
{
    public bool DrawListDirty;
    public bool NeedsMoreRequests;
}
