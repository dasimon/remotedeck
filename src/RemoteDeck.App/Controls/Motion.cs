using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace RemoteDeck.App.Controls;

/// <summary>
/// The application's motion, in one place: two gestures — something arriving, something leaving —
/// and the three durations the theme sheet allows.
/// </summary>
/// <remarks>
/// <para>
/// Durations are not written here. They are the <c>RdMotion*</c> tokens of <c>Theme.xaml</c>, a
/// closed set like the radii and the heights, and this class reads them so that a duration outside
/// the set is a styling defect rather than a local decision.
/// </para>
/// <para>
/// Windows' own setting — <em>Accessibility › Visual effects › Animation effects</em> — is honoured
/// through <see cref="SystemParameters.ClientAreaAnimation"/>: off, every duration is zero, the end
/// state is applied at once and the continuation runs synchronously. Nothing else in the
/// application has to know.
/// </para>
/// <para>
/// Only <see cref="UIElement.Opacity"/> and a <see cref="TranslateTransform"/> are ever animated:
/// both are render-only, so layout never runs during a gesture and nothing under a
/// <c>WindowsFormsHost</c> is asked to move. A gesture started on an element that is still in the
/// middle of the previous one supersedes it — the earlier continuation is dropped, never run late
/// against the new state.
/// </para>
/// </remarks>
internal static class Motion
{
    /// <summary>Whether Windows asks for animations at all.</summary>
    public static bool Enabled => SystemParameters.ClientAreaAnimation;

    /// <summary>For something leaving: 80 ms by the sheet.</summary>
    public static TimeSpan Fast => Read("RdMotionFast");

    /// <summary>For something arriving: 150 ms by the sheet.</summary>
    public static TimeSpan Normal => Read("RdMotion");

    /// <summary>For a surface moving as a whole: 220 ms by the sheet. Nothing uses it yet; it is
    /// the ceiling.</summary>
    public static TimeSpan Slow => Read("RdMotionSlow");

    /// <summary>
    /// Brings <paramref name="element"/> in: from transparent and <paramref name="offsetY"/> pixels
    /// away to opaque and in place, easing out — fast at first, settling at the end, the way a
    /// thing that arrives should.
    /// </summary>
    public static void Arrive(FrameworkElement element, double offsetY, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(element);
        Supersede(element);
        var transform = TranslateOf(element);

        if (!Enabled || duration == TimeSpan.Zero)
        {
            element.Opacity = 1;
            transform.Y = 0;
            return;
        }

        // From the current values, not from a fixed start: an element still fading out is caught
        // where it is rather than snapped back to invisible first.
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        element.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(1, duration) { EasingFunction = ease }, HandoffBehavior.SnapshotAndReplace);
        if (Math.Abs(transform.Y) < 0.01)
        {
            transform.Y = offsetY;
        }

        transform.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, duration) { EasingFunction = ease }, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>
    /// Takes <paramref name="element"/> out: to transparent, easing in — slow at first, gone at the
    /// end — then runs <paramref name="then"/>, which is where the caller collapses, closes or
    /// hides. Runs it at once when animations are off.
    /// </summary>
    public static void Leave(FrameworkElement element, TimeSpan duration, Action then)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(then);
        var generation = Supersede(element);

        if (!Enabled || duration == TimeSpan.Zero)
        {
            element.Opacity = 0;
            then();
            return;
        }

        var fade = new DoubleAnimation(0, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        fade.Completed += (_, _) =>
        {
            // A newer gesture on the same element owns it now; this continuation is stale.
            if (GetGeneration(element) == generation)
            {
                then();
            }
        };
        element.BeginAnimation(UIElement.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>Stops animating, leaving the element opaque and in place. For an element that is
    /// about to be shown by other means and must not carry a half-finished gesture.</summary>
    public static void Settle(FrameworkElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        Supersede(element);
        element.BeginAnimation(UIElement.OpacityProperty, null);
        var transform = TranslateOf(element);
        transform.BeginAnimation(TranslateTransform.YProperty, null);
        element.Opacity = 1;
        transform.Y = 0;
    }

    private static TimeSpan Read(string key)
    {
        // The sheet is the source. A missing key is a defect in the sheet, and zero — no motion at
        // all — is the failure that cannot make the interface wrong, only plainer.
        return System.Windows.Application.Current?.TryFindResource(key) is Duration { HasTimeSpan: true } d
            ? d.TimeSpan
            : TimeSpan.Zero;
    }

    private static TranslateTransform TranslateOf(FrameworkElement element)
    {
        if (element.RenderTransform is TranslateTransform existing)
        {
            return existing;
        }

        // The element's own transform is replaced, deliberately: nothing in this application
        // sets a RenderTransform for any other purpose, and a TransformGroup would make the
        // translate part findable only by position.
        var transform = new TranslateTransform();
        element.RenderTransform = transform;
        return transform;
    }

    private static readonly DependencyProperty GenerationProperty = DependencyProperty.RegisterAttached(
        "MotionGeneration", typeof(int), typeof(Motion), new PropertyMetadata(0));

    private static int GetGeneration(DependencyObject element) => (int)element.GetValue(GenerationProperty);

    /// <summary>Marks a new gesture on the element, so the previous one's continuation knows it is stale.</summary>
    private static int Supersede(DependencyObject element)
    {
        var next = GetGeneration(element) + 1;
        element.SetValue(GenerationProperty, next);
        return next;
    }
}
