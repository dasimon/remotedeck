namespace RemoteDeck.Core.Settings;

/// <summary>
/// Where a detached session window was last seen, in virtual-screen coordinates. Persisted per
/// connection in settings.json: reopening the same machine puts its window back where it was.
/// </summary>
public sealed record DetachedWindowPlacement(double Left, double Top, double Width, double Height, bool FullScreen);
