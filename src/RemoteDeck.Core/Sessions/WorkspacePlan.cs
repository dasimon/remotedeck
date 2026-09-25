using RemoteDeck.Core.Model;
using RemoteDeck.Core.Settings;

namespace RemoteDeck.Core.Sessions;

/// <summary>What to do with a connection to open a workspace.</summary>
public enum WorkspaceActionKind
{
    /// <summary>The session is already there, in the right container: bring it to the front.</summary>
    Activate = 0,
    /// <summary>The session is detached and the workspace wants it detached: bring it to the front and move it.</summary>
    MoveDetached = 1,
    /// <summary>The session is docked and the workspace wants it detached.</summary>
    Detach = 2,
    /// <summary>The session is detached and the workspace wants it docked.</summary>
    Reattach = 3,
    /// <summary>No session: open a tab.</summary>
    OpenDocked = 4,
    /// <summary>No session: open, then detach.</summary>
    OpenDetached = 5,
}

/// <summary>
/// One step of opening a workspace. <paramref name="Placement"/> is the rectangle already fitted to
/// the screens present, or <c>null</c> when the workspace has none or the one it had belongs to a
/// screen that is gone.
///
/// What the caller does with it depends on the action. For <see cref="WorkspaceActionKind.OpenDetached"/>
/// and <see cref="WorkspaceActionKind.Detach"/>, a <c>null</c> makes it fall back to the
/// per-connection memory, then to centering, exactly like an ordinary detach. For
/// <see cref="WorkspaceActionKind.MoveDetached"/>, there is no fallback: the window is already on
/// screen somewhere, and with no rectangle to impose the workspace leaves it where it is rather
/// than moving it to a place it did not ask for.
/// </summary>
public sealed record WorkspaceAction(WorkspaceActionKind Kind, long ConnectionId, DetachedWindowPlacement? Placement);

/// <summary>
/// Turns a workspace into a list of actions, given the connections that still exist, the sessions
/// already open and the screens present right now.
///
/// Pure: no I/O, no UI, no state. That is this type's reason to exist — the decision can be tested,
/// the WPF execution cannot.
/// </summary>
public static class WorkspacePlan
{
    /// <param name="existingConnectionIds">The connections that still exist in the database. An item
    /// that is not among them is silently skipped: the cascade may have removed it between the read
    /// and here, and that is a race, not a user error.</param>
    /// <param name="openSessions">Connection id → whether its session is detached. A connection has at
    /// most one session, an invariant of <c>SessionsViewModel.Find</c>.</param>
    public static IReadOnlyList<WorkspaceAction> Build(
        Workspace workspace,
        IReadOnlySet<long> existingConnectionIds,
        IReadOnlyDictionary<long, bool> openSessions,
        IReadOnlyList<ScreenBounds> screens)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(existingConnectionIds);
        ArgumentNullException.ThrowIfNull(openSessions);
        ArgumentNullException.ThrowIfNull(screens);

        var actions = new List<WorkspaceAction>();

        foreach (var item in workspace.Items.OrderBy(i => i.Ordinal))
        {
            if (!existingConnectionIds.Contains(item.ConnectionId)) continue;

            // Fitted here once and for all: no branch below needs to know what a screen is. A docked
            // item has no placement, and ScreenFit returns null for a null input.
            var placement = item.Detached ? ScreenFit.Choose(item.Placement, screens) : null;

            var kind = openSessions.TryGetValue(item.ConnectionId, out bool isDetached)
                ? (isDetached, item.Detached) switch
                {
                    (true, true) => WorkspaceActionKind.MoveDetached,
                    (false, true) => WorkspaceActionKind.Detach,
                    (true, false) => WorkspaceActionKind.Reattach,
                    (false, false) => WorkspaceActionKind.Activate,
                }
                : item.Detached ? WorkspaceActionKind.OpenDetached : WorkspaceActionKind.OpenDocked;

            actions.Add(new WorkspaceAction(kind, item.ConnectionId, placement));
        }

        return actions;
    }
}
