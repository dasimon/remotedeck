using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RemoteDeck.App.Resources;
using RemoteDeck.Core.Search;

namespace RemoteDeck.App.ViewModels;

/// <summary>
/// Backs the Ctrl+K command palette: the entries it was given, the query typed over them, and the
/// one entry that is selected.
///
/// The view-model owns no UI and knows nothing about what an entry <em>does</em>: the shell builds
/// the <see cref="PaletteItem"/> list and acts on the chosen <see cref="PaletteItem.Id"/>. Nothing
/// here touches the database — the list is a snapshot handed in at construction.
/// </summary>
/// <remarks>
/// Filtering is synchronous and has no debounce, unlike <see cref="ConnectionListViewModel"/>: the
/// items are already in memory, there are a few dozen of them, and a palette that lags a keystroke
/// behind picks the wrong entry when Enter follows the last letter closely.
/// </remarks>
public sealed partial class CommandPaletteViewModel : ObservableObject
{
    /// <summary>The unfiltered entries, in the order the shell built them. Never mutated.</summary>
    private readonly IReadOnlyList<PaletteItem> _items;

    public CommandPaletteViewModel(IReadOnlyList<PaletteItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        _items = items;
        Refresh();
    }

    /// <summary>The entries kept by the current query, best first. Rebuilt in place on every keystroke.</summary>
    public ObservableCollection<PaletteMatch> Results { get; } = [];

    [ObservableProperty] private string _searchText = "";

    /// <summary>The entry Enter would run. Two-way bound to the list's selection.</summary>
    [ObservableProperty] private PaletteMatch? _selected;

    /// <summary>True when <see cref="Results"/> is empty, so the view can show <see cref="EmptyMessage"/>.</summary>
    [ObservableProperty] private bool _isEmpty;

    /// <summary>What to show over an empty list. Only ever seen with a query typed: an empty palette
    /// without one would mean the shell offered no command at all, which it never does.</summary>
    [ObservableProperty] private string _emptyMessage = "";

    /// <summary>The id the shell must act on, or <c>null</c> when nothing is selected.</summary>
    public string? SelectedId => Selected?.Item.Id;

    /// <summary>
    /// Re-applies the query to the snapshot and selects the best entry. Cheap: no I/O.
    /// </summary>
    /// <remarks>
    /// The selection deliberately jumps back to the top on every keystroke rather than following the
    /// previously selected entry: after typing a letter the best match <em>is</em> the first row, and
    /// keeping a now-worse entry selected would make Enter run something the query no longer describes.
    ///
    /// <para>The filtered entries are re-laid group by group before they land in
    /// <see cref="Results"/>. The view draws them under headings, and a grouped WPF view gathers each
    /// group at the position where it was first met — so a score order that alternates between groups
    /// would be drawn in an order this collection does not have, and
    /// <see cref="MoveSelection(int)"/>, which walks it by index, would send the selection jumping
    /// around the list. Regrouping here keeps one order for both. It costs nothing in ranking:
    /// <c>GroupBy</c> preserves the order groups and entries were seen in, so the best match is still
    /// the first row, and its group is still the first group.</para>
    /// </remarks>
    public void Refresh()
    {
        var matches = PaletteFilter.Apply(_items, SearchText)
            .GroupBy(m => m.Item.Group, StringComparer.Ordinal)
            .SelectMany(g => g);

        Results.Clear();
        foreach (var match in matches) Results.Add(match);

        Selected = Results.FirstOrDefault();
        IsEmpty = Results.Count == 0;
        EmptyMessage = string.IsNullOrWhiteSpace(SearchText)
            ? Strings.Palette_EmptyNothingToRun
            : Text.Of(Strings.Palette_EmptyNoMatch, SearchText.Trim());
    }

    /// <summary>
    /// Moves the selection by <paramref name="delta"/> entries, wrapping around both ends — ↓ on the
    /// last row lands on the first, which is what a palette of a handful of rows should do. A no-op
    /// on an empty result list, and with nothing selected it takes the first (↓) or last (↑) entry.
    /// </summary>
    public void MoveSelection(int delta)
    {
        if (Results.Count == 0)
        {
            Selected = null;
            return;
        }

        if (delta == 0) return;

        int current = Selected is null ? -1 : Results.IndexOf(Selected);
        if (current < 0)
        {
            Selected = delta > 0 ? Results[0] : Results[^1];
            return;
        }

        // long arithmetic then a double modulo: delta is caller-supplied (int.MinValue would
        // overflow the addition) and C#'s % keeps the sign of the left operand.
        int count = Results.Count;
        Selected = Results[(int)(((current + (long)delta) % count + count) % count)];
    }

    /// <summary>Typing filters immediately; see the class remarks for why there is no debounce.</summary>
    partial void OnSearchTextChanged(string value) => Refresh();
}
