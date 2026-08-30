using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
// UseWindowsForms is on for the MSTSCLib host, so System.Drawing is in the implicit usings:
// alias the WPF types the renderer needs to avoid CS0104.
using Point = System.Windows.Point;
using SystemColors = System.Windows.SystemColors;

namespace RemoteDeck.App.Controls;

/// <summary>
/// Placeholder text for the native <see cref="PasswordBox"/>, which — unlike WPF-UI's TextBox —
/// has no <c>PlaceholderText</c> of its own.
///
/// The text is drawn by an <see cref="Adorner"/> on top of the box, so the box's own content is
/// never touched and the secret path stays exactly as it was. Emptiness is decided from
/// <see cref="PasswordBox.SecurePassword"/>.Length only: the managed <c>Password</c> string is
/// never read (spec D5).
///
/// Usage: <c>&lt;PasswordBox controls:PasswordPlaceholder.Text="password" /&gt;</c>.
/// </summary>
public static class PasswordPlaceholder
{
    /// <summary>Opacity applied to the box foreground, to approximate WPF-UI's muted placeholder.</summary>
    private const double PlaceholderOpacity = 0.6;

    /// <summary>The placeholder shown while the box is empty. Empty or null disables the feature.</summary>
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(PasswordPlaceholder), new FrameworkPropertyMetadata(null, OnTextChanged));

    public static string? GetText(DependencyObject element) => (string?)element.GetValue(TextProperty);

    public static void SetText(DependencyObject element, string? value) => element.SetValue(TextProperty, value);

    /// <summary>Back-reference to the live adorner, so it can be refreshed and removed.</summary>
    private static readonly DependencyProperty AdornerProperty = DependencyProperty.RegisterAttached(
        "Adorner", typeof(PlaceholderAdorner), typeof(PasswordPlaceholder));

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box) return;

        // Idempotent rewire: the property can be re-set (e.g. by a binding) at any time.
        box.Loaded -= OnLoaded;
        box.Unloaded -= OnUnloaded;
        box.PasswordChanged -= OnPasswordChanged;
        box.SizeChanged -= OnSizeChanged;

        if (string.IsNullOrEmpty((string?)e.NewValue))
        {
            Detach(box);
            return;
        }

        box.Loaded += OnLoaded;
        box.Unloaded += OnUnloaded;
        box.PasswordChanged += OnPasswordChanged;
        box.SizeChanged += OnSizeChanged;

        if (box.IsLoaded) Attach(box);
        else Refresh(box);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e) => Attach((PasswordBox)sender);

    private static void OnUnloaded(object sender, RoutedEventArgs e) => Detach((PasswordBox)sender);

    private static void OnPasswordChanged(object sender, RoutedEventArgs e) => Refresh((PasswordBox)sender);

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e) => Refresh((PasswordBox)sender);

    private static void Attach(PasswordBox box)
    {
        if (box.GetValue(AdornerProperty) is PlaceholderAdorner) { Refresh(box); return; }

        var layer = AdornerLayer.GetAdornerLayer(box);
        if (layer is null)
        {
            // The adorner layer is not in the tree yet (rare, but a template can arrive late).
            // Retry once on the next layout pass rather than losing the placeholder for good.
            void Retry(object? s, EventArgs e)
            {
                box.LayoutUpdated -= Retry;
                if (!string.IsNullOrEmpty(GetText(box)) && box.IsLoaded) Attach(box);
            }
            box.LayoutUpdated += Retry;
            return;
        }

        var adorner = new PlaceholderAdorner(box);
        layer.Add(adorner);
        box.SetValue(AdornerProperty, adorner);
        Refresh(box);
    }

    private static void Detach(PasswordBox box)
    {
        if (box.GetValue(AdornerProperty) is not PlaceholderAdorner adorner) return;
        AdornerLayer.GetAdornerLayer(box)?.Remove(adorner);
        box.ClearValue(AdornerProperty);
    }

    private static void Refresh(PasswordBox box)
    {
        if (box.GetValue(AdornerProperty) is not PlaceholderAdorner adorner) return;
        adorner.Visibility = IsEmpty(box) ? Visibility.Visible : Visibility.Collapsed;
        adorner.InvalidateVisual();
    }

    /// <summary>
    /// True when the box holds no secret. Reads <see cref="PasswordBox.SecurePassword"/> — which hands
    /// out a fresh copy on every read — exactly once, and disposes it immediately.
    /// </summary>
    private static bool IsEmpty(PasswordBox box)
    {
        using var secure = box.SecurePassword;
        return secure.Length == 0;
    }

    private sealed class PlaceholderAdorner : Adorner
    {
        private readonly PasswordBox _box;

        internal PlaceholderAdorner(PasswordBox box) : base(box)
        {
            _box = box;
            IsHitTestVisible = false;
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            var text = GetText(_box);
            if (string.IsNullOrEmpty(text)) return;

            var brush = _box.Foreground ?? SystemColors.GrayTextBrush;
            var size = _box.RenderSize;
            double left = _box.BorderThickness.Left + _box.Padding.Left;
            double right = _box.BorderThickness.Right + _box.Padding.Right;
            double available = size.Width - left - right;
            if (available <= 0) return;

            var formatted = new FormattedText(
                text,
                CultureInfo.CurrentUICulture,
                _box.FlowDirection,
                new Typeface(_box.FontFamily, _box.FontStyle, _box.FontWeight, _box.FontStretch),
                _box.FontSize,
                brush,
                VisualTreeHelper.GetDpi(_box).PixelsPerDip)
            {
                MaxTextWidth = available,
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };

            double top = Math.Max(_box.BorderThickness.Top + _box.Padding.Top, (size.Height - formatted.Height) / 2);
            drawingContext.PushOpacity(PlaceholderOpacity);
            drawingContext.DrawText(formatted, new Point(left, top));
            drawingContext.Pop();
        }
    }
}
