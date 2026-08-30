namespace RemoteDeck.Core.Settings;

/// <summary>User-interface state persisted between runs (spec §7). Never holds secrets.</summary>
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
}
