namespace RemoteDeck.Core.Model;

/// <summary>How the remote desktop follows the window size. Values are persisted; never renumber.</summary>
public enum DisplayMode
{
    /// <summary>Remote resolution follows the window (UpdateSessionDisplaySettings). Default.</summary>
    Dynamic = 0,
    /// <summary>Fixed remote resolution, image scaled to fit (SmartSizing).</summary>
    Scaled = 1,
    /// <summary>Fixed remote resolution, scrollbars when the window is smaller.</summary>
    Fixed = 2,
    /// <summary>
    /// The session uses every local monitor (UseMultimon, like <c>mstsc /multimon</c>) and opens
    /// detached in full screen. The layout is fixed at connection time.
    /// </summary>
    AllMonitors = 3,
}
