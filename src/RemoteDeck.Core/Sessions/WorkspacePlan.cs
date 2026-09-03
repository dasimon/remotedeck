using RemoteDeck.Core.Model;
using RemoteDeck.Core.Settings;

namespace RemoteDeck.Core.Sessions;

/// <summary>Ce qu'il faut faire d'une connexion pour monter un espace.</summary>
public enum WorkspaceActionKind
{
    /// <summary>La session est déjà là et déjà dans le bon conteneur : l'amener au premier plan.</summary>
    Activate = 0,
    /// <summary>La session est détachée et l'espace la veut détachée : l'amener au premier plan et la replacer.</summary>
    MoveDetached = 1,
    /// <summary>La session est ancrée et l'espace la veut détachée.</summary>
    Detach = 2,
    /// <summary>La session est détachée et l'espace la veut ancrée.</summary>
    Reattach = 3,
    /// <summary>Pas de session : ouvrir un onglet.</summary>
    OpenDocked = 4,
    /// <summary>Pas de session : ouvrir puis détacher.</summary>
    OpenDetached = 5,
}

/// <summary>
/// Une action du montage. <paramref name="Placement"/> est le rectangle déjà ajusté aux écrans
/// présents, ou <c>null</c> quand l'espace n'en a pas ou que celui qu'il avait appartient à un
/// écran disparu.
///
/// Ce que l'appelant en fait dépend de l'action. Pour <see cref="WorkspaceActionKind.OpenDetached"/>
/// et <see cref="WorkspaceActionKind.Detach"/>, un <c>null</c> le fait retomber sur la mémorisation
/// par connexion, puis sur le centrage, exactement comme un détachement ordinaire. Pour
/// <see cref="WorkspaceActionKind.MoveDetached"/>, il n'y a pas de repli : la fenêtre est déjà à
/// l'écran quelque part, et sans rectangle à lui imposer l'espace la laisse où elle est plutôt que
/// de la déplacer vers une place qu'il n'a pas demandée.
/// </summary>
public sealed record WorkspaceAction(WorkspaceActionKind Kind, long ConnectionId, DetachedWindowPlacement? Placement);

/// <summary>
/// Traduit un espace en une liste d'actions, en fonction des connexions qui existent encore, des
/// sessions déjà ouvertes et des écrans présents maintenant (spec espaces §4.1).
///
/// Pur : pas d'E/S, pas d'interface, pas d'état. C'est la raison d'être de ce type — la décision se
/// teste, l'exécution WPF non.
/// </summary>
public static class WorkspacePlan
{
    /// <param name="existingConnectionIds">Les connexions qui existent encore en base. Un item qui
    /// n'y est pas est ignoré en silence : la cascade a pu le retirer entre la lecture et ici, et
    /// c'est une course, pas une erreur de l'utilisateur.</param>
    /// <param name="openSessions">Id de connexion → la session est-elle détachée. Une connexion a au
    /// plus une session, invariant de <c>SessionsViewModel.Find</c>.</param>
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

            // Ajusté ici une fois pour toutes : aucune branche ci-dessous n'a à savoir ce qu'est un
            // écran. Un item ancré n'a pas de place, et ScreenFit rend null sur une entrée nulle.
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
