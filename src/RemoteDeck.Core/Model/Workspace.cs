using RemoteDeck.Core.Settings;

namespace RemoteDeck.Core.Model;

/// <summary>
/// A set of connections and the layout they had when the user captured it (workspaces spec §3).
/// The name is unique: it is the only way to pick a workspace in the palette.
/// </summary>
public sealed class Workspace
{
    public long Id { get; set; }

    public string Name { get; set; } = "";

    /// <summary>Connect the sessions when the workspace is opened. Set at capture, nowhere else (§4.4).</summary>
    public bool AutoConnect { get; set; } = true;

    public DateTime CreatedUtc { get; set; }

    /// <summary>The workspace's connections, in tab strip order. Never null.</summary>
    public List<WorkspaceItem> Items { get; set; } = [];
}

/// <summary>
/// A connection in a workspace, and the state the workspace wants it in.
/// </summary>
/// <remarks>
/// <see cref="Placement"/> is null for a docked item — a docked session has no window to place —
/// and may also be null for a detached item whose placement was never saved; the per-connection
/// memory in <c>settings.json</c> is then the fallback (spec §7).
/// </remarks>
public sealed class WorkspaceItem
{
    public long ConnectionId { get; set; }

    /// <summary>Position in the tab strip, starting at 0.</summary>
    public int Ordinal { get; set; }

    public bool Detached { get; set; }

    public DetachedWindowPlacement? Placement { get; set; }
}
