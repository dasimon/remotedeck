namespace RemoteDeck.Core.Settings;

/// <summary>User-interface state persisted between runs. Never holds secrets.</summary>
public sealed class AppSettings
{
    /// <summary>Width of the connection pane, in device-independent pixels.</summary>
    public double PaneWidth { get; set; } = 300;

    public bool PaneCollapsed { get; set; }

    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    /// <summary>Connection selected when the app was last closed, if it still exists.</summary>
    public long? LastConnectionId { get; set; }

    /// <summary>
    /// Geometry of each detached session window, keyed by connection id written as invariant text.
    /// A string key on purpose: System.Text.Json only round-trips dictionaries keyed by string
    /// without a converter. Never null after a Load().
    /// </summary>
    public Dictionary<string, DetachedWindowPlacement> DetachedWindows { get; set; } = [];

    /// <summary>
    /// Reopen at startup the sessions that were there at close. False by default: launching the
    /// app must not connect to anything until the user has asked for it.
    /// </summary>
    public bool RestoreLastSession { get; set; }

    /// <summary>
    /// Ask GitHub for a newer release shortly after startup, then once a day while the app stays
    /// open. True by default, including for a file written before this setting existed (a missing
    /// key keeps the initializer). The manual check in the About window ignores it. A development
    /// build never checks, whatever it says.
    /// </summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>
    /// What was open at the last clean close, in tab strip order. Rewritten on every clean close and
    /// only then: a crash leaves the previous one in place, which is the useful behaviour. Never
    /// null after a <c>Load()</c>.
    /// </summary>
    public List<LastSessionEntry> LastSession { get; set; } = [];
}

/// <summary>
/// A session from the last close. Same fields as a <c>WorkspaceItem</c> minus the workspace: the
/// restore is window state, not composed content, hence its place here rather than in the database
///.
/// </summary>
public sealed class LastSessionEntry
{
    public long ConnectionId { get; set; }

    public int Ordinal { get; set; }

    public bool Detached { get; set; }

    public DetachedWindowPlacement? Placement { get; set; }
}
