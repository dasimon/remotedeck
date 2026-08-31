using System.Collections.ObjectModel;
using System.ComponentModel;
// System.IO is not implicit here: UseWindowsForms replaces the SDK's default global usings with its
// own set, which drops it. Every other file in the app that touches the disk imports it the same way.
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Win32;
using RemoteDeck.Core.Data;
using RemoteDeck.Core.Import;
using RemoteDeck.Core.Model;

namespace RemoteDeck.App.ViewModels;

/// <summary>
/// One line of the import preview: a candidate the reader produced, the verdict on it, and the tick
/// that decides whether it is written. The tick is the decision, the status only the advice — a row
/// the user ticks is imported whatever its status says.
/// </summary>
public sealed partial class ImportRow : ObservableObject
{
    /// <summary>The row is not already known, neither in the database nor earlier in the same batch.</summary>
    public const string NewStatus = "New";

    /// <summary>A saved connection already uses this host and port.</summary>
    public const string AlreadyImportedStatus = "Already imported";

    /// <summary>What the reader proposes. Never carries a password: <see cref="ImportCandidate"/> has no such field.</summary>
    public required ImportCandidate Candidate { get; init; }

    /// <summary>Whether <see cref="ImportViewModel.Import"/> will write this row.</summary>
    [ObservableProperty] private bool _selected;

    /// <summary><see cref="NewStatus"/>, <see cref="AlreadyImportedStatus"/> or <c>Duplicate of &lt;name&gt;</c>.</summary>
    [ObservableProperty] private string _status = NewStatus;

    /// <summary>Extra context for the tooltip — which saved connection a duplicate matches, typically.</summary>
    [ObservableProperty] private string _detail = "";

    /// <summary><c>Duplicate of &lt;name&gt;</c>, where <paramref name="name"/> is the earlier row in this same batch.</summary>
    public static string DuplicateOf(string name) => $"Duplicate of {name}";

    public string Name => Candidate.Name;

    /// <summary>Always <c>host:port</c>, so two rows that differ only by port are told apart at a glance.</summary>
    public string Address => $"{Candidate.Host}:{Candidate.Port}";

    /// <summary>The file name, or <c>mstsc registry</c>. The full path goes to the tooltip instead of the column.</summary>
    public string Source => Path.IsPathRooted(Candidate.Source) ? Path.GetFileName(Candidate.Source) : Candidate.Source;

    /// <summary>True while <see cref="Status"/> is <see cref="NewStatus"/>; drives <c>Select all new</c>.</summary>
    public bool IsNew => Status == NewStatus;

    /// <summary>
    /// The one thing the reader found that the import deliberately leaves behind: the source's user name,
    /// with its domain when there is one. An identity belongs to a credential, and import creates none, so
    /// the name is said here — visible in the preview, before anything is written — and stored nowhere.
    /// </summary>
    /// <remarks>
    /// Computed rather than pushed into <see cref="ImportCandidate.Warnings"/>: that list is the reader's
    /// account of what it could not use, and this is the importer's account of what it chose not to carry.
    /// The tooltip shows both, so the distinction costs the user nothing.
    /// </remarks>
    public string? CredentialWarning
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Candidate.UserName)) return null;

            var user = string.IsNullOrWhiteSpace(Candidate.Domain)
                ? Candidate.UserName
                : $"{Candidate.Domain}\\{Candidate.UserName}";
            return $"User name “{user}” is not imported: attach a credential instead.";
        }
    }

    /// <summary>Everything the row has to say that does not fit a column: the source, the duplicate it
    /// matches, the user name that is not carried, and what the reader had to drop.</summary>
    public string ToolTipText
    {
        get
        {
            var lines = new List<string> { Candidate.Source };
            if (Detail.Length > 0) lines.Add(Detail);
            if (CredentialWarning is { } credential) lines.Add(credential);
            lines.AddRange(Candidate.Warnings);
            return string.Join(Environment.NewLine, lines);
        }
    }

    partial void OnStatusChanged(string value)
    {
        OnPropertyChanged(nameof(IsNew));
    }

    partial void OnDetailChanged(string value)
    {
        OnPropertyChanged(nameof(ToolTipText));
    }
}

/// <summary>
/// Backs the import window: it reads a folder of <c>.rdp</c> files or the servers Remote Desktop
/// Connection remembers, shows what it found, and writes the rows the user keeps.
///
/// Nothing is written before <see cref="Import"/>, and what is written is a connection and only a
/// connection: no password is ever read from a source and no credential is ever created, so an
/// imported row starts with <see cref="Connection.CredentialId"/> null.
/// </summary>
public sealed partial class ImportViewModel : ObservableObject
{
    /// <summary>Where Remote Desktop Connection remembers its servers, under <c>HKEY_CURRENT_USER</c>.</summary>
    public const string RegistryKeyPath = @"Software\Microsoft\Terminal Server Client\Servers";

    /// <summary>The only value of a server subkey worth reading; absent on most of them.</summary>
    private const string UserNameHintValue = "UsernameHint";

    private const string RdpPattern = "*.rdp";

    private readonly ConnectionRepository _repository;

    /// <summary>What the last load found, before the statuses are counted. <see cref="Summary"/> is this plus the tally.</summary>
    private string _head = "";

    public ImportViewModel(ConnectionRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
    }

    /// <summary>The preview, rebuilt from scratch by each load: a source is browsed, not accumulated.</summary>
    public ObservableCollection<ImportRow> Rows { get; } = [];

    /// <summary>One sentence about the last load — what was read, and how the rows break down.</summary>
    [ObservableProperty] private string _summary = "Choose a source above to see what can be imported.";

    /// <summary>How many rows are ticked. Drives <see cref="ImportButtonText"/> and <see cref="CanImport"/>.</summary>
    [ObservableProperty] private int _selectedCount;

    public string ImportButtonText => SelectedCount == 1 ? "Import 1 connection" : $"Import {SelectedCount} connections";

    public bool CanImport => SelectedCount > 0;

    /// <summary>
    /// Reads every <c>.rdp</c> file directly inside <paramref name="folder"/> — subfolders are not
    /// searched — and replaces <see cref="Rows"/> with what they yield. Enumeration and parsing run off
    /// the UI thread; a file that cannot be read is counted here, in the delegate that knows it failed,
    /// and skipped by <see cref="RdpFileImporter.ParseFolder"/> so one bad file does not cost the folder.
    /// </summary>
    /// <exception cref="Exception">Whatever <see cref="Directory.EnumerateFiles"/> throws when the folder
    /// itself cannot be listed: that is a failed load, not a partial one, and the window reports it.</exception>
    public async Task LoadFromFolderAsync(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);

        var (candidates, files, unreadable) = await Task.Run(() =>
        {
            var found = Directory.EnumerateFiles(folder, RdpPattern, SearchOption.TopDirectoryOnly).ToList();
            var failed = 0;

            IEnumerable<string> ReadLines(string path)
            {
                try
                {
                    // ReadAllLines, not ReadLines: a lazy enumerable would fail inside the parser,
                    // where this counter can no longer see it.
                    return File.ReadAllLines(path);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                            or System.Security.SecurityException or NotSupportedException)
                {
                    failed++;
                    throw;
                }
            }

            return (RdpFileImporter.ParseFolder(folder, ReadLines, found), found.Count, failed);
        }).ConfigureAwait(true);

        var parts = new List<string> { $"{Plural(files, ".rdp file")} in {folder}" };
        if (unreadable > 0) parts.Add($"{unreadable} could not be read");

        var hostless = files - unreadable - candidates.Count;
        if (hostless > 0) parts.Add($"{hostless} carried no usable address");

        _head = string.Join(", ", parts) + ".";
        Populate(candidates);
    }

    /// <summary>
    /// Reads the servers Remote Desktop Connection remembers and replaces <see cref="Rows"/> with them.
    /// A missing key means the client has never been used or its history was cleared: an empty preview,
    /// not an error. The registry holds no port and no password, so every row keeps the default port and
    /// proposes no credential.
    /// </summary>
    public void LoadFromRegistry()
    {
        var entries = ReadRememberedServers();
        _head = entries.Count == 0
            ? "Remote Desktop Connection remembers no server."
            : $"{Plural(entries.Count, "server")} remembered by Remote Desktop Connection.";
        Populate(MstscRegistryImporter.FromServers(entries));
    }

    /// <summary>
    /// Writes every ticked row, in order, and returns how many were written. The status is advice, not a
    /// veto: ticking a duplicate is a deliberate act — two rows on the same host with different names or
    /// options are legitimate — so what is ticked is what is imported.
    /// </summary>
    /// <remarks>
    /// The statuses are recomputed afterwards even when an insert throws halfway, so the rows already
    /// written show as <see cref="ImportRow.AlreadyImportedStatus"/> and a second click cannot double them.
    /// </remarks>
    public int Import()
    {
        var imported = 0;
        try
        {
            foreach (var row in Rows.Where(r => r.Selected).ToList())
            {
                _repository.Insert(ToConnection(row.Candidate));
                imported++;
            }
        }
        finally
        {
            if (imported > 0) ApplyStatuses();
        }

        return imported;
    }

    /// <summary>Ticks every row still marked new, and only those.</summary>
    public void SelectAllNew()
    {
        foreach (var row in Rows) row.Selected = row.IsNew;
    }

    /// <summary>Unticks everything.</summary>
    public void ClearSelection()
    {
        foreach (var row in Rows) row.Selected = false;
    }

    /// <summary>
    /// The remembered servers, one per subkey, with the <c>UsernameHint</c> value when it carries a
    /// string. Public and static so the shape of the registry read is testable by eye in one place.
    /// </summary>
    public static IReadOnlyList<(string Host, string? UserName)> ReadRememberedServers()
    {
        using var servers = Registry.CurrentUser.OpenSubKey(RegistryKeyPath);
        if (servers is null) return [];

        var entries = new List<(string, string?)>();
        foreach (var host in servers.GetSubKeyNames())
        {
            using var server = servers.OpenSubKey(host);
            entries.Add((host, server?.GetValue(UserNameHintValue) as string));
        }

        return entries;
    }

    /// <summary>Replaces the rows, then dates them against the database and against each other.</summary>
    private void Populate(IReadOnlyList<ImportCandidate> candidates)
    {
        foreach (var row in Rows) row.PropertyChanged -= OnRowChanged;
        Rows.Clear();

        foreach (var candidate in candidates)
        {
            var row = new ImportRow { Candidate = candidate };
            row.PropertyChanged += OnRowChanged;
            Rows.Add(row);
        }

        ApplyStatuses();
    }

    /// <summary>
    /// Re-dates every row against the saved connections and against the rows above it, and ticks the new
    /// ones. <c>(Host, Port)</c> is the identity, compared without case: that is what makes two rows the
    /// same target, whatever they are called.
    /// </summary>
    private void ApplyStatuses()
    {
        var saved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var connection in _repository.GetAll())
        {
            saved.TryAdd(AddressKey(connection.Host, connection.Port), connection.Name);
        }

        var batch = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int fresh = 0, already = 0, duplicate = 0;

        foreach (var row in Rows)
        {
            var key = AddressKey(row.Candidate.Host, row.Candidate.Port);
            if (saved.TryGetValue(key, out var savedName))
            {
                row.Status = ImportRow.AlreadyImportedStatus;
                row.Detail = $"Already saved as “{savedName}”.";
                already++;
            }
            else if (batch.TryGetValue(key, out var firstName))
            {
                row.Status = ImportRow.DuplicateOf(firstName);
                row.Detail = "Another row above targets the same host and port.";
                duplicate++;
            }
            else
            {
                batch[key] = row.Candidate.Name;
                row.Status = ImportRow.NewStatus;
                row.Detail = "";
                fresh++;
            }

            // A duplicate stays untickable-by-default rather than unavailable: the user decides.
            row.Selected = row.IsNew;
        }

        var tally = new List<string> { $"{fresh} new" };
        if (already > 0) tally.Add($"{already} already imported");
        if (duplicate > 0) tally.Add($"{duplicate} duplicate in this batch");

        Summary = Rows.Count == 0
            ? $"{_head} Nothing to import.".Trim()
            : $"{_head} {Plural(Rows.Count, "connection")}: {string.Join(", ", tally)}.".Trim();

        Recount();
    }

    /// <summary>
    /// Everything an external source may legitimately fill, and nothing else: no group, no favourite, no
    /// notes — that field is the user's own free text and import has no business writing in it — and above
    /// all no credential. The user name a source carries is dropped here and named in the preview instead,
    /// by <see cref="ImportRow.CredentialWarning"/>.
    /// </summary>
    private static Connection ToConnection(ImportCandidate candidate) => new()
    {
        Name = ClampName(candidate.Name),
        Host = candidate.Host,
        Port = candidate.Port,
        DisplayMode = candidate.DisplayMode,
        FixedWidth = candidate.FixedWidth,
        FixedHeight = candidate.FixedHeight,
        RedirectClipboard = candidate.RedirectClipboard,
        RedirectDrives = candidate.RedirectDrives,
        RedirectPrinters = candidate.RedirectPrinters,
        RedirectAudio = candidate.RedirectAudio,
        UseWebAccount = candidate.UseWebAccount,
        AuthenticationLevel = candidate.AuthenticationLevel,
    };

    /// <summary>The file name a <c>.rdp</c> carries is not bound by the editor's limit; the column is.</summary>
    private static string ClampName(string name)
    {
        var trimmed = name.Trim();
        return trimmed.Length <= ConnectionRules.MaxNameLength ? trimmed : trimmed[..ConnectionRules.MaxNameLength];
    }

    private static string AddressKey(string host, int port) => $"{host}:{port}";

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ImportRow.Selected)) Recount();
    }

    private void Recount() => SelectedCount = Rows.Count(r => r.Selected);

    partial void OnSelectedCountChanged(int value)
    {
        OnPropertyChanged(nameof(ImportButtonText));
        OnPropertyChanged(nameof(CanImport));
    }
}
