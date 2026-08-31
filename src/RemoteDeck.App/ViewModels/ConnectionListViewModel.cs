using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteDeck.App.Resources;
using RemoteDeck.Core.Data;
using RemoteDeck.Core.Model;
using RemoteDeck.Core.Search;

namespace RemoteDeck.App.ViewModels;

/// <summary>
/// One row of the connection pane: a <see cref="ConnectionMatch"/> plus the few plain properties the
/// XAML binds to. <see cref="Group"/> exists because <c>PropertyGroupDescription</c> needs a real
/// bindable property to group on, and <see cref="ConnectionMatch"/> (a Core type) has none.
/// Immutable: the view-model rebuilds the whole list on every refresh.
/// </summary>
public sealed class ConnectionItem(ConnectionMatch match, string group)
{
    public ConnectionMatch Match { get; } = match;
    public string Group { get; } = group;
    public Connection Connection => Match.Connection;
    public string Name => Match.Connection.Name;
    public string Host => Match.Connection.Host;
    public bool IsFavorite => Match.Connection.IsFavorite;
}

/// <summary>
/// Backs the connection pane: the saved connections, the search box, and the intents the shell acts on.
///
/// The view-model owns no UI: it raises <see cref="ConnectRequested"/>, <see cref="EditRequested"/> and
/// <see cref="DeleteRequested"/> and lets the shell (task 6) open sessions, editors and confirmations.
/// It reads the database only in <see cref="Reload"/>; <see cref="Refresh"/> filters the in-memory
/// snapshot, so typing never touches SQLite.
/// </summary>
public sealed partial class ConnectionListViewModel : ObservableObject
{
    /// <summary>Group name for favorites. The star keeps it visually distinct; the leading character
    /// also happens to sort it first, but group order is decided by <see cref="Refresh"/>, not by the name.</summary>
    public static string FavoritesGroup => Strings.Pane_GroupFavorites;

    /// <summary>Group name for connections whose <see cref="Connection.GroupName"/> is blank.</summary>
    public static string UngroupedGroup => Strings.Pane_GroupUngrouped;

    /// <summary>Search debounce. Short enough to feel instant, long enough that a fast typist
    /// filters once instead of once per keystroke.</summary>
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(120);

    private readonly ConnectionRepository _repository;
    private readonly DispatcherTimer _searchDebounce;

    /// <summary>The last snapshot read from the database. <see cref="Refresh"/> filters this, not the table.</summary>
    private IReadOnlyList<Connection> _all = [];

    public ConnectionListViewModel(ConnectionRepository repository)
    {
        _repository = repository;
        _searchDebounce = new DispatcherTimer { Interval = SearchDelay };
        _searchDebounce.Tick += OnSearchDebounceTick;
        Reload();
    }

    /// <summary>The rows currently shown, already grouped-ordered (favorites first). Rebuilt in place so the
    /// <c>CollectionViewSource</c> bound to it — and its grouping — survives every refresh.</summary>
    public ObservableCollection<ConnectionItem> Items { get; } = [];

    /// <summary>Raised when the user asks to open a session (Enter, or a double-click).</summary>
    public event Action<Connection>? ConnectRequested;

    /// <summary>Raised when the user asks for the editor. <c>null</c> means "new connection".</summary>
    public event Action<Connection?>? EditRequested;

    /// <summary>Raised when the user asks to delete. The shell owns the confirmation.</summary>
    public event Action<Connection>? DeleteRequested;

    /// <summary>Raised when the user asks for the import window. The shell owns it, like every other window.</summary>
    public event Action? ImportRequested;

    [ObservableProperty] private string _searchText = "";

    [ObservableProperty] private ConnectionItem? _selected;

    /// <summary>True when <see cref="Items"/> is empty, so the view can show <see cref="EmptyMessage"/>.</summary>
    [ObservableProperty] private bool _isEmpty = true;

    /// <summary>What to show over an empty list. The wording depends on why it is empty: an unfiltered
    /// empty list is a first-run state, a filtered one is a failed search.</summary>
    [ObservableProperty] private string _emptyMessage = DefaultEmptyMessage;

    private static string DefaultEmptyMessage => Strings.Pane_EmptyDefault;

    /// <summary>The connection behind <see cref="Selected"/>, for callers that only care about the model.</summary>
    public Connection? SelectedConnection => Selected?.Connection;

    /// <summary>Re-reads the table and re-applies the current search. Call after an add, edit or delete.</summary>
    public void Reload()
    {
        _all = _repository.GetAll();
        Refresh();
    }

    /// <summary>Re-applies the search to the in-memory snapshot. Cheap: no I/O.</summary>
    public void Refresh()
    {
        _searchDebounce.Stop();

        var matches = ConnectionFilter.Apply(_all, SearchText);

        // Group order in a CollectionViewSource is the order in which the groups are first met, so the
        // only way to pin "★ Favorites" to the top is to order the items themselves. LINQ's OrderBy is
        // stable, so within each half the filter's own ranking (score, then name) is preserved.
        var ordered = matches
            .Select(m => new ConnectionItem(m, GroupOf(m)))
            .OrderBy(i => i.IsFavorite ? 0 : 1);

        var previous = Selected?.Connection.Id;

        Items.Clear();
        foreach (var item in ordered) Items.Add(item);

        // Keep the selection on the same connection across a refresh when it survived the filter, and
        // otherwise fall back to the first row: Enter must connect something right after a search.
        Selected = Items.FirstOrDefault(i => i.Connection.Id == previous) ?? Items.FirstOrDefault();

        IsEmpty = Items.Count == 0;
        EmptyMessage = string.IsNullOrWhiteSpace(SearchText)
            ? DefaultEmptyMessage
            : Text.Of(Strings.Pane_EmptyNoMatch, SearchText.Trim());
    }

    /// <summary>The group a match belongs to: favorites win over the connection's own group name.</summary>
    public static string GroupOf(ConnectionMatch match)
    {
        ArgumentNullException.ThrowIfNull(match);
        if (match.Connection.IsFavorite) return FavoritesGroup;
        return string.IsNullOrWhiteSpace(match.Connection.GroupName) ? UngroupedGroup : match.Connection.GroupName;
    }

    [RelayCommand]
    private void New() => EditRequested?.Invoke(null);

    [RelayCommand]
    private void Import() => ImportRequested?.Invoke();

    [RelayCommand]
    private void ConnectSelected()
    {
        if (Selected is { } item) ConnectRequested?.Invoke(item.Connection);
    }

    [RelayCommand]
    private void EditSelected()
    {
        if (Selected is { } item) EditRequested?.Invoke(item.Connection);
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        if (Selected is { } item) DeleteRequested?.Invoke(item.Connection);
    }

    /// <summary>Typing restarts the debounce; the filter runs once the user pauses.</summary>
    partial void OnSearchTextChanged(string value)
    {
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void OnSearchDebounceTick(object? sender, EventArgs e) => Refresh();
}
