using System.Windows;
using RemoteDeck.App.Resources;
using RemoteDeck.Core.Sessions;

namespace RemoteDeck.App.Controls;

/// <summary>
/// What a connection row says about the VPN profile it needs: nothing when it needs none, a quiet
/// <em>VPN</em> when the tunnel is up, and a warning glyph beside the word when it is down.
/// </summary>
/// <remarks>
/// <para>
/// The problem state is the one that draws the eye; the expected state stays muted, because a pane
/// full of green badges for tunnels that are simply up would be noise. The glyph is the second
/// channel beside the colour, for the same reason <see cref="StatusTag"/> spells its state out.
/// </para>
/// <para>
/// The colour lives in the implicit style in <c>Resources/Theme.xaml</c>, like the state pill's, so
/// it follows a theme switch.
/// </para>
/// </remarks>
public sealed class VpnTag : System.Windows.Controls.Control
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(VpnState), typeof(VpnTag),
        new PropertyMetadata(VpnState.NotRequired, OnChanged));

    public static readonly DependencyProperty ProfileProperty = DependencyProperty.Register(
        nameof(Profile), typeof(string), typeof(VpnTag),
        new PropertyMetadata("", OnChanged));

    private static readonly DependencyPropertyKey DescriptionPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(Description), typeof(string), typeof(VpnTag), new PropertyMetadata(""));

    /// <summary>The sentence behind the tag — its tooltip and its accessible name.</summary>
    public static readonly DependencyProperty DescriptionProperty = DescriptionPropertyKey.DependencyProperty;

    public VpnState State
    {
        get => (VpnState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    /// <summary>The profile's name, as the connection spells it.</summary>
    public string Profile
    {
        get => (string)GetValue(ProfileProperty);
        set => SetValue(ProfileProperty, value);
    }

    public string Description => (string)GetValue(DescriptionProperty);

    /// <summary>The word on the tag. One word in both languages, but a resource like every other.</summary>
    public static string Word => Strings.Pane_VpnTag;

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var tag = (VpnTag)d;
        tag.SetValue(DescriptionPropertyKey, tag.State switch
        {
            VpnState.Connected => Text.Of(Strings.Pane_VpnUp, tag.Profile),
            VpnState.NotConnected => Text.Of(Strings.Pane_VpnDown, tag.Profile),
            _ => "",
        });
    }
}
