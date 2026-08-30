using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using RemoteDeck.Core.Search;

namespace RemoteDeck.App.Controls;

/// <summary>
/// A <see cref="TextBlock"/> that renders the match ranges produced by
/// <see cref="RemoteDeck.Core.Search.ConnectionFilter"/> in the accent colour, so the user sees
/// which letters of a connection name their query actually hit.
///
/// The inline runs are rebuilt whenever <see cref="TextBlock.Text"/> or <see cref="Ranges"/>
/// changes. Ranges are treated as untrusted input: they are clamped to the text, empty and
/// out-of-range ones are dropped, and overlapping ones are merged — the filter is free to emit
/// per-character ranges (its subsequence path does), and a run per character would be wasteful.
/// </summary>
public sealed class HighlightTextBlock : TextBlock
{
    /// <summary>Theme key for the highlight colour. Resolved through
    /// <see cref="FrameworkElement.SetResourceReference"/> (the code equivalent of a
    /// <c>DynamicResource</c>) so it follows a light/dark switch at runtime.</summary>
    private const string HighlightBrushKey = "AccentTextFillColorPrimaryBrush";

    /// <summary>The ranges to highlight, in <see cref="TextBlock.Text"/> coordinates. Null or empty
    /// renders the text as a single plain run.</summary>
    public static readonly DependencyProperty RangesProperty = DependencyProperty.Register(
        nameof(Ranges), typeof(IReadOnlyList<MatchRange>), typeof(HighlightTextBlock),
        new FrameworkPropertyMetadata(null, OnContentChanged));

    public IReadOnlyList<MatchRange>? Ranges
    {
        get => (IReadOnlyList<MatchRange>?)GetValue(RangesProperty);
        set => SetValue(RangesProperty, value);
    }

    /// <summary>Guards the rebuild: filling <see cref="TextBlock.Inlines"/> writes back to
    /// <see cref="TextBlock.Text"/>, which re-enters the change callback below.</summary>
    private bool _rebuilding;

    // TextBlock seals OnPropertyChanged, so Text is hooked by overriding its metadata. WPF chains
    // the callbacks: TextBlock's own runs first (it turns Text into a single run), ours then
    // splits that run into the highlighted and plain pieces.
    static HighlightTextBlock()
    {
        TextProperty.OverrideMetadata(typeof(HighlightTextBlock), new FrameworkPropertyMetadata(OnContentChanged));
    }

    private static void OnContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((HighlightTextBlock)d).Rebuild();

    private void Rebuild()
    {
        if (_rebuilding) return;

        _rebuilding = true;
        try
        {
            var text = Text ?? string.Empty;
            Inlines.Clear();
            if (text.Length == 0) return;

            var spans = Normalize(Ranges, text.Length);
            if (spans.Count == 0)
            {
                Inlines.Add(new Run(text));
                return;
            }

            int cursor = 0;
            foreach (var (start, end) in spans)
            {
                if (start > cursor) Inlines.Add(new Run(text[cursor..start]));

                var hit = new Run(text[start..end]) { FontWeight = FontWeights.SemiBold };
                hit.SetResourceReference(TextElement.ForegroundProperty, HighlightBrushKey);
                Inlines.Add(hit);

                cursor = end;
            }

            if (cursor < text.Length) Inlines.Add(new Run(text[cursor..]));
        }
        finally
        {
            _rebuilding = false;
        }
    }

    /// <summary>
    /// Clamps the ranges to <paramref name="length"/>, drops the ones that end up empty, and merges
    /// the overlapping and adjacent ones into ascending, disjoint half-open spans.
    /// </summary>
    private static List<(int Start, int End)> Normalize(IReadOnlyList<MatchRange>? ranges, int length)
    {
        var spans = new List<(int Start, int End)>();
        if (ranges is null || ranges.Count == 0) return spans;

        // long arithmetic: Start + Length is caller-supplied and may overflow int.
        var clamped = new List<(int Start, int End)>(ranges.Count);
        foreach (var range in ranges)
        {
            if (range.Length <= 0) continue;

            long start = Math.Max(0L, range.Start);
            long end = Math.Min(length, (long)range.Start + range.Length);
            if (end <= start) continue;

            clamped.Add(((int)start, (int)end));
        }

        clamped.Sort(static (a, b) => a.Start.CompareTo(b.Start));

        foreach (var span in clamped)
        {
            if (spans.Count > 0 && span.Start <= spans[^1].End)
            {
                spans[^1] = (spans[^1].Start, Math.Max(spans[^1].End, span.End));
            }
            else
            {
                spans.Add(span);
            }
        }

        return spans;
    }
}
