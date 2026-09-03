using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteDeck.App.Controls;
using RemoteDeck.App.Resources;
using RemoteDeck.Core.Data;
using RemoteDeck.Core.Model;
using RemoteDeck.Core.Search;

namespace RemoteDeck.App.ViewModels;

/// <summary>
/// One row of the connection pane: a <see cref="ConnectionMatch"/> plus the few plain properties the
/// XAML binds to. <see cref="Group"/> exists because <c>PropertyGroupDescription</c> needs a real
/// bindable property to group on, and <see cref="ConnectionMatch"/> (a Core type) has none.
/// </summary>
/// <remarks>
/// Everything but <see cref="Status"/> is fixed at construction — the view-model rebuilds the whole
/// list on every refresh. <see cref="Status"/> is the exception because a session changes state under
/// a row that is not being rebuilt, so it has to be observable rather than read once.
/// </remarks>
public sealed partial class ConnectionItem : ObservableObject
{
    public ConnectionItem(ConnectionMatch match, string group)
    {
        Match = match;
        Group = group;
    }

    public ConnectionMatch Match { get; }
    public string Group { get; }
    public Connection Connection => Match.Connection;
    public string Name => Match.Connection.Name;
    public string Host => Match.Connection.Host;
    public bool IsFavorite => Match.Connection.IsFavorite;

    /// <summary>What the row's state pill says. <see cref="ConnectionStatus.None"/> — the default —
    /// hides the pill, which is the right answer for a connection nobody has opened.</summary>
    [ObservableProperty] private ConnectionStatus _status;
}

/// <summary>
/// One saved workspace, as the pane shows it: a name and how many connections it holds.
///
/// It carries the id rather than the <see cref="Workspace"/> itself. The row only ever needs to say
/// which workspace was clicked; re-reading it from the database at that moment is also what keeps a
/// stale row — one whose workspace was emptied by a connection being deleted — from acting on a
/// snapshot taken minutes ago.
/// </summary>
public sealed record WorkspaceListItem(long Id, string Name, int ConnectionCount)
{
    /// <summary>Reuses the palette's wording so the same workspace reads the same in both places,
    /// and pluralises: a workspace of one connection reading "1 connections" is the kind of detail
    /// that makes an interface look unfinished.</summary>
    public string Subtitle =>
        Text.Plural(ConnectionCount, Strings.Workspace_CountOne, Strings.Workspace_CountMany, ConnectionCount);
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

    /// <summary>
    /// The saved workspaces, shown above the connections. Deliberately a list of its own rather than
    /// rows mixed into <see cref="Items"/>: a workspace is not a connection, and the row template a
    /// connection needs — status pill, accent rail, match highlighting — has nothing to say about one.
    /// </summary>
    public ObservableCollection<WorkspaceListItem> Workspaces { get; } = [];

    /// <summary>True when there is at least one workspace, so the section can disappear entirely
    /// rather than leave a heading over nothing.</summary>
    [ObservableProperty] private bool _hasWorkspaces;

    /// <summary>
    /// Where the workspaces come from. The shell owns <c>WorkspaceRepository</c> and sets this, the
    /// same way it sets <see cref="StatusProvider"/>: the pane reads what it is given and owns no
    /// repository of its own. Left null, the section simply never appears.
    /// </summary>
    public Func<IReadOnlyList<Workspace>>? WorkspacesProvider { get; set; }

    /// <summary>The user asked to open a workspace. The shell mounts it.</summary>
    public event Action<long>? WorkspaceOpenRequested;

    /// <summary>The user asked to delete a workspace. The shell owns the confirmation, exactly as it
    /// does for a connection.</summary>
    public event Action<long>? WorkspaceDeleteRequested;

    /// <summary>Raised when the user asks to open a session (Enter, or a double-click).</summary>
    public event Action<Connection>? ConnectRequested;

    /// <summary>Raised when the user asks for the editor. <c>null</c> means "new connection".</summary>
    public event Action<Connection?>? EditRequested;

    /// <summary>Raised when the user asks to delete. The shell owns the confirmation.</summary>
    public event Action<Connection>? DeleteRequested;

    /// <summary>Raised when the user asks for the import window. The shell owns it, like every other window.</summary>
    public event Action? ImportRequested;

    /// <summary>The favorite flag was toggled on a connection. Carries the value it should now have,
    /// read from the row the user acted on — the shell writes it and reloads.</summary>
    public event Action<Connection, bool>? FavoriteToggleRequested;

    /// <summary>
    /// How a row learns whether its connection currently has a session, given the connection's id.
    /// The shell sets it: the pane knows nothing about sessions, and must not start to.
    /// </summary>
    /// <remarks>
    /// A provider rather than a subscription, and a pull rather than a push, because the list is
    /// rebuilt wholesale on every refresh: a row created two lines ago has to be able to ask.
    /// Left null, every row simply stays <see cref="ConnectionStatus.None"/> and shows no pill.
    /// </remarks>
    public Func<long, ConnectionStatus>? StatusProvider { get; set; }

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
        ReloadWorkspaces();
        Refresh();
    }

    /// <summary>
    /// Re-reads the workspaces. Called by <see cref="Reload"/>, and on its own by the shell after a
    /// capture — that changes the workspaces without touching a single connection.
    /// </summary>
    public void ReloadWorkspaces()
    {
        Workspaces.Clear();
        foreach (var workspace in WorkspacesProvider?.Invoke() ?? [])
        {
            Workspaces.Add(new WorkspaceListItem(workspace.Id, workspace.Name, workspace.Items.Count));
        }

        HasWorkspaces = Workspaces.Count > 0;
    }

    /// <summary>A click on a workspace row.</summary>
    [RelayCommand]
    private void OpenWorkspace(WorkspaceListItem? item)
    {
        if (item is not null) WorkspaceOpenRequested?.Invoke(item.Id);
    }

    /// <summary>Its context menu's only destructive entry.</summary>
    [RelayCommand]
    private void DeleteWorkspace(WorkspaceListItem? item)
    {
        if (item is not null) WorkspaceDeleteRequested?.Invoke(item.Id);
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

        RefreshStatuses();
    }

    /// <summary>Re-asks <see cref="StatusProvider"/> for every visible row. Called after a rebuild and
    /// whenever a session changes state; cheap enough to run on either, since it touches only the rows
    /// the filter left standing and an unchanged assignment raises nothing.</summary>
    public void RefreshStatuses()
    {
        if (StatusProvider is not { } provider) return;

        foreach (var item in Items)
        {
            item.Status = provider(item.Connection.Id);
        }
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

    /// <summary>Flips the favorite flag of the selected row. The new value is computed here rather
    /// than by the shell, so the menu's checkmark and the write always agree on what was on screen.</summary>
    [RelayCommand]
    private void ToggleFavoriteSelected()
    {
        if (Selected is { } item) FavoriteToggleRequested?.Invoke(item.Connection, !item.IsFavorite);
    }

    /// <summary>Typing restarts the debounce; the filter runs once the user pauses.</summary>
    partial void OnSearchTextChanged(string value)
    {
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void OnSearchDebounceTick(object? sender, EventArgs e) => Refresh();
}
