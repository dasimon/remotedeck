using System.Text.Json;
using System.Text.Json.Serialization;
using RemoteDeck.Core.Data;
using RemoteDeck.Core.Model;

namespace RemoteDeck.Core.Transfer;

/// <summary>
/// Writes and reads configuration files, and turns one into a plan before anything is written:
/// the shell shows the plan, and only an accepted plan is applied.
/// </summary>
public static class ConfigurationTransfer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>The document for what this machine holds, secrets excluded by construction.</summary>
    public static ConfigurationDocument Build(
        IEnumerable<Connection> connections, IEnumerable<Credential> credentials, IEnumerable<Workspace> workspaces,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(workspaces);

        var credentialList = credentials.ToList();
        var labels = credentialList.ToDictionary(c => c.Id, c => c.Label);

        return new ConfigurationDocument
        {
            ExportedUtc = nowUtc,
            Credentials = [.. credentialList.Select(c => new ExportedCredential(c.Label, c.UserName, c.Domain))],
            Connections = [.. connections.Select(c => new ExportedConnection
            {
                Key = c.Id,
                Name = c.Name,
                Host = c.Host,
                Port = c.Port,
                GroupName = c.GroupName,
                Credential = c.CredentialId is { } id && labels.TryGetValue(id, out var label) ? label : null,
                IsFavorite = c.IsFavorite,
                DisplayMode = c.DisplayMode,
                FixedWidth = c.FixedWidth,
                FixedHeight = c.FixedHeight,
                RedirectClipboard = c.RedirectClipboard,
                RedirectDrives = c.RedirectDrives,
                RedirectPrinters = c.RedirectPrinters,
                RedirectAudio = c.RedirectAudio,
                AdminSession = c.AdminSession,
                UseWebAccount = c.UseWebAccount,
                WebAccountUpn = c.WebAccountUpn,
                AuthenticationLevel = c.AuthenticationLevel,
                VpnProfile = c.VpnProfile,
                AutoRaiseVpn = c.AutoRaiseVpn,
                Notes = c.Notes,
            })],
            Workspaces = [.. workspaces.Select(w => new ExportedWorkspace(w.Name, w.AutoConnect,
                [.. w.Items.Select(i => new ExportedWorkspaceItem(i.ConnectionId, i.Ordinal, i.Detached, i.Placement))]))],
        };
    }

    public static string Serialize(ConfigurationDocument document) => JsonSerializer.Serialize(document, Options);

    /// <summary>
    /// Reads a configuration file. Throws <see cref="InvalidDataException"/> for anything that is not
    /// one this build understands: malformed JSON, another format, or a newer version.
    /// </summary>
    public static ConfigurationDocument Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        ConfigurationDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<ConfigurationDocument>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Not valid JSON: {ex.Message}", ex);
        }

        if (document is null || document.Format != ConfigurationDocument.FormatName)
        {
            throw new InvalidDataException("Not a RemoteDeck configuration file.");
        }

        if (document.Version > ConfigurationDocument.CurrentVersion)
        {
            throw new InvalidDataException(
                $"Written by a newer RemoteDeck (format version {document.Version}); this one reads up to {ConfigurationDocument.CurrentVersion}.");
        }

        return document;
    }

    /// <summary>
    /// What importing <paramref name="document"/> would do here, without doing it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A connection already present — same name, host and port, ignoring case — is not added again,
    /// so importing the same file twice adds nothing; the workspaces of the file still point at it.
    /// A connection the editor would refuse is not added either.
    /// </para>
    /// <para>
    /// Credentials are matched by label. One missing here leaves its connections without a
    /// credential, and is named in the plan so the user can create it: a password cannot travel.
    /// </para>
    /// <para>
    /// A workspace whose name is taken is not added: replacing the local one from a file would lose
    /// what the user arranged here without a word.
    /// </para>
    /// </remarks>
    public static ImportPlan Plan(
        ConfigurationDocument document,
        IEnumerable<Connection> existingConnections,
        IEnumerable<Credential> existingCredentials,
        IEnumerable<Workspace> existingWorkspaces)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(existingConnections);
        ArgumentNullException.ThrowIfNull(existingCredentials);
        ArgumentNullException.ThrowIfNull(existingWorkspaces);

        var present = new Dictionary<(string, string, int), long>();
        foreach (var c in existingConnections)
        {
            present.TryAdd(Identity(c.Name, c.Host, c.Port), c.Id);
        }

        var credentialIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in existingCredentials)
        {
            credentialIds.TryAdd(c.Label, c.Id);
        }

        var workspaceNames = new HashSet<string>(existingWorkspaces.Select(w => w.Name), StringComparer.OrdinalIgnoreCase);

        var toAdd = new List<PlannedConnection>();
        var existingByKey = new Dictionary<long, long>();
        var plannedByIdentity = new Dictionary<(string, string, int), long>();
        var aliases = new Dictionary<long, long>();
        var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        int alreadyPresent = 0, invalid = 0, weakened = 0;

        foreach (var e in document.Connections)
        {
            if (ConnectionRules.Validate(e.Name, e.Host, e.Port, e.DisplayMode, e.FixedWidth, e.FixedHeight).Count > 0)
            {
                invalid++;
                continue;
            }

            var identity = Identity(e.Name, e.Host, e.Port);
            if (present.TryGetValue(identity, out var existingId))
            {
                existingByKey[e.Key] = existingId;
                alreadyPresent++;
                continue;
            }

            // A second row of the file with the same identity is the same connection: added once,
            // and the workspaces that name either key open that one.
            if (plannedByIdentity.TryGetValue(identity, out var firstKey))
            {
                aliases[e.Key] = firstKey;
                alreadyPresent++;
                continue;
            }

            long? credentialId = null;
            if (!string.IsNullOrWhiteSpace(e.Credential))
            {
                if (credentialIds.TryGetValue(e.Credential.Trim(), out var found)) credentialId = found;
                else missing.Add(e.Credential.Trim());
            }

            if (e.AuthenticationLevel == 0 || e.RedirectDrives) weakened++;

            plannedByIdentity[identity] = e.Key;
            toAdd.Add(new PlannedConnection(e.Key, ToConnection(e, credentialId)));
        }

        var workspaces = new List<ExportedWorkspace>();
        int workspacesPresent = 0;
        foreach (var w in document.Workspaces)
        {
            if (string.IsNullOrWhiteSpace(w.Name) || !workspaceNames.Add(w.Name.Trim()))
            {
                workspacesPresent++;
                continue;
            }

            workspaces.Add(w);
        }

        return new ImportPlan(toAdd, existingByKey, aliases, workspaces, alreadyPresent, invalid, workspacesPresent,
            [.. missing], weakened);
    }

    /// <summary>
    /// Writes an accepted plan: the connections first, then the workspaces, whose items are mapped to
    /// the ids the connections received. An item whose connection is neither added nor present is
    /// dropped, and a workspace left empty by that is not written.
    /// </summary>
    /// <remarks>
    /// Not one transaction: each repository opens its own connection. A failure halfway leaves what
    /// was written, and <paramref name="result"/> — the caller's, so it survives the exception —
    /// counts it. A second import of the same file then adds only what is still missing.
    /// </remarks>
    public static void Apply(
        ImportPlan plan, ConnectionRepository connections, WorkspaceRepository workspaces, ImportResult result)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(workspaces);
        ArgumentNullException.ThrowIfNull(result);

        var ids = new Dictionary<long, long>(plan.ExistingByKey);

        foreach (var planned in plan.Connections)
        {
            ids[planned.Key] = connections.Insert(planned.Connection);
            result.ConnectionsAdded++;
        }

        foreach (var (alias, key) in plan.KeyAliases)
        {
            if (ids.TryGetValue(key, out var id)) ids[alias] = id;
        }

        foreach (var w in plan.Workspaces)
        {
            var items = w.Items
                .Where(i => ids.ContainsKey(i.Connection))
                .OrderBy(i => i.Ordinal)
                .Select((i, ordinal) => new WorkspaceItem
                {
                    ConnectionId = ids[i.Connection],
                    Ordinal = ordinal,
                    Detached = i.Detached,
                    Placement = i.Placement,
                })
                .ToList();

            if (items.Count == 0) continue;

            workspaces.Save(new Workspace { Name = w.Name.Trim(), AutoConnect = w.AutoConnect, Items = items });
            result.WorkspacesAdded++;
        }
    }

    private static (string, string, int) Identity(string name, string host, int port) =>
        (name.Trim().ToUpperInvariant(), host.Trim().ToUpperInvariant(), port);

    private static Connection ToConnection(ExportedConnection e, long? credentialId) => new()
    {
        Name = e.Name.Trim(),
        Host = e.Host.Trim(),
        Port = e.Port,
        GroupName = e.GroupName?.Trim() ?? "",
        CredentialId = credentialId,
        IsFavorite = e.IsFavorite,
        DisplayMode = e.DisplayMode,
        FixedWidth = e.FixedWidth,
        FixedHeight = e.FixedHeight,
        RedirectClipboard = e.RedirectClipboard,
        RedirectDrives = e.RedirectDrives,
        RedirectPrinters = e.RedirectPrinters,
        RedirectAudio = e.RedirectAudio,
        AdminSession = e.AdminSession,
        UseWebAccount = e.UseWebAccount,
        WebAccountUpn = e.WebAccountUpn,
        AuthenticationLevel = e.AuthenticationLevel,
        VpnProfile = e.VpnProfile,
        AutoRaiseVpn = e.AutoRaiseVpn,
        Notes = e.Notes ?? "",
    };
}

/// <param name="Key">The connection's key in the file, which the file's workspaces refer to.</param>
public sealed record PlannedConnection(long Key, Connection Connection);

/// <summary>What an import would do, for the user to accept or refuse before anything is written.</summary>
/// <param name="ExistingByKey">File keys of connections already present here, mapped to their local ids.</param>
/// <param name="KeyAliases">File keys of rows repeating an earlier row of the file, mapped to that row's key.</param>
/// <param name="MissingCredentials">Labels the file names that no local credential carries.</param>
/// <param name="WeakenedSecurity">New connections with server authentication off or every drive shared.</param>
public sealed record ImportPlan(
    IReadOnlyList<PlannedConnection> Connections,
    IReadOnlyDictionary<long, long> ExistingByKey,
    IReadOnlyDictionary<long, long> KeyAliases,
    IReadOnlyList<ExportedWorkspace> Workspaces,
    int ConnectionsAlreadyPresent,
    int ConnectionsInvalid,
    int WorkspacesAlreadyPresent,
    IReadOnlyList<string> MissingCredentials,
    int WeakenedSecurity)
{
    public bool HasSomethingToAdd => Connections.Count > 0 || Workspaces.Count > 0;
}

/// <summary>What an applied plan wrote.</summary>
public sealed class ImportResult
{
    public int ConnectionsAdded { get; set; }

    public int WorkspacesAdded { get; set; }
}
