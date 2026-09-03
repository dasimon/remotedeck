using RemoteDeck.Core.Settings;

namespace RemoteDeck.Core.Model;

/// <summary>
/// Un jeu de connexions et la disposition qu'elles avaient quand l'utilisateur l'a capturé
/// (spec espaces §3). Le nom est unique : c'est la seule façon de désigner un espace dans la palette.
/// </summary>
public sealed class Workspace
{
    public long Id { get; set; }

    public string Name { get; set; } = "";

    /// <summary>Connecter les sessions au montage. Réglé à la capture, nulle part ailleurs (§4.4).</summary>
    public bool AutoConnect { get; set; } = true;

    public DateTime CreatedUtc { get; set; }

    /// <summary>Les connexions de l'espace, dans l'ordre du ruban. Jamais nul.</summary>
    public List<WorkspaceItem> Items { get; set; } = [];
}

/// <summary>
/// Une connexion dans un espace, et l'état dans lequel l'espace la veut.
/// </summary>
/// <remarks>
/// <see cref="Placement"/> est nul pour un item ancré — une session ancrée n'a pas de fenêtre à
/// placer — et peut l'être aussi pour un item détaché dont la place n'a jamais été enregistrée ;
/// la mémorisation par connexion de <c>settings.json</c> sert alors de repli (spec §7).
/// </remarks>
public sealed class WorkspaceItem
{
    public long ConnectionId { get; set; }

    /// <summary>Position dans le ruban, à partir de 0.</summary>
    public int Ordinal { get; set; }

    public bool Detached { get; set; }

    public DetachedWindowPlacement? Placement { get; set; }
}
