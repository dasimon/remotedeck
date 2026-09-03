namespace RemoteDeck.Core.Settings;

/// <summary>User-interface state persisted between runs (spec §7). Never holds secrets.</summary>
public sealed class AppSettings
{
    /// <summary>Width of the connection pane, in device-independent pixels.</summary>
    public double PaneWidth { get; set; } = 300;

    public bool PaneCollapsed { get; set; }

    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    /// <summary>Connection selected when the app was last closed, if it still exists.</summary>
    public long? LastConnectionId { get; set; }

    /// <summary>
    /// Geometry of each detached session window, keyed by connection id written as invariant text.
    /// A string key on purpose: System.Text.Json only round-trips dictionaries keyed by string
    /// without a converter. Never null after a Load().
    /// </summary>
    public Dictionary<string, DetachedWindowPlacement> DetachedWindows { get; set; } = [];

    /// <summary>
    /// Rouvrir au démarrage les sessions qui étaient là à la fermeture. Faux par défaut : lancer
    /// l'application ne doit se connecter à rien tant que l'utilisateur ne l'a pas demandé
    /// (spec espaces §7).
    /// </summary>
    public bool RestoreLastSession { get; set; }

    /// <summary>
    /// Ce qui était ouvert à la dernière fermeture propre, dans l'ordre du ruban. Réécrit à chaque
    /// fermeture propre et seulement là : une fermeture par crash laisse la précédente, ce qui est
    /// le comportement utile. Jamais nul après un <c>Load()</c>.
    /// </summary>
    public List<LastSessionEntry> LastSession { get; set; } = [];
}

/// <summary>
/// Une session de la dernière fermeture. Mêmes champs qu'un <c>WorkspaceItem</c> moins l'espace :
/// la reprise est de l'état de fenêtrage, pas du contenu composé, d'où sa place ici et non en base
/// (spec espaces §3).
/// </summary>
public sealed class LastSessionEntry
{
    public long ConnectionId { get; set; }

    public int Ordinal { get; set; }

    public bool Detached { get; set; }

    public DetachedWindowPlacement? Placement { get; set; }
}
