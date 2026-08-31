using System.Windows;
using RemoteDeck.App.Rdp;
using RemoteDeck.App.Resources;

namespace RemoteDeck.App.Controls;

/// <summary>
/// What a connection row reports about the session behind it. Three settled states and an absence:
/// a saved connection with no session of its own has nothing to say, and says nothing.
/// </summary>
public enum ConnectionStatus
{
    /// <summary>No session for this connection, or one still making its first attempt. No tag.</summary>
    None,

    /// <summary>A session is up.</summary>
    Connected,

    /// <summary>A session dropped and is being retried.</summary>
    Reconnecting,

    /// <summary>A session exists but is down: it failed, ended, or is on its way out.</summary>
    Offline,
}

/// <summary>
/// The state pill: a coloured dot followed by the state spelled out. The word is the point —
/// the dot alone asks the reader to tell red from green, which not every reader can, so the two
/// carry the same message twice (plan, global constraint 5).
/// </summary>
/// <remarks>
/// The control holds the state and the word; the colour lives in the implicit style in
/// <c>Resources/Theme.xaml</c>, where a trigger per state maps it to <c>RdOk</c>, <c>RdWarn</c> or
/// <c>RdBad</c> through a <c>DynamicResource</c>. Keeping the brush there rather than here is what
/// makes the pill follow a light/dark switch: a brush resolved in C# would freeze at first render.
/// </remarks>
public sealed class StatusTag : System.Windows.Controls.Control
{
    /// <summary>The state to report. Drives both the word and, through the style, the colour.</summary>
    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status), typeof(ConnectionStatus), typeof(StatusTag),
        new PropertyMetadata(ConnectionStatus.None, OnStatusChanged));

    private static readonly DependencyPropertyKey TextPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(Text), typeof(string), typeof(StatusTag), new PropertyMetadata(""));

    /// <summary>The localised word for <see cref="Status"/>. Read-only: it is a function of the state.</summary>
    public static readonly DependencyProperty TextProperty = TextPropertyKey.DependencyProperty;

    public ConnectionStatus Status
    {
        get => (ConnectionStatus)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    public string Text => (string)GetValue(TextProperty);

    /// <summary>
    /// The tag a session state deserves. <see cref="SessionState.Connecting"/> maps to
    /// <see cref="ConnectionStatus.None"/> on purpose: calling a first attempt "Reconnecting" would
    /// be false, and the three words of the plan leave no fourth one to say it with.
    /// </summary>
    public static ConnectionStatus For(SessionState state) => state switch
    {
        SessionState.Connected => ConnectionStatus.Connected,
        SessionState.Interrupted or SessionState.Reconnecting => ConnectionStatus.Reconnecting,
        SessionState.Connecting => ConnectionStatus.None,
        _ => ConnectionStatus.Offline,
    };

    private static void OnStatusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((StatusTag)d).SetValue(TextPropertyKey, LabelOf((ConnectionStatus)e.NewValue));

    // Session_StateConnected is the session bar's own word, reused rather than duplicated: the two
    // places must never drift into saying "Connected" and "Online" about the same session.
    private static string LabelOf(ConnectionStatus status) => status switch
    {
        ConnectionStatus.Connected => Strings.Session_StateConnected,
        ConnectionStatus.Reconnecting => Strings.Pane_StatusReconnecting,
        ConnectionStatus.Offline => Strings.Pane_StatusOffline,
        _ => "",
    };
}
