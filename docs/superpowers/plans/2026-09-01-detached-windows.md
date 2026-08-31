# Fenêtres de session détachées — plan d'implémentation

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Sortir une session RDP vivante de la fenêtre principale vers sa propre fenêtre (et la remettre), afin d'afficher plusieurs bureaux distants en plein écran sur plusieurs écrans — sans jamais couper la session.

**Architecture:** Le détachement est un **déplacement du `WindowsFormsHost`** d'un conteneur WPF à l'autre (technique B, mesurée par la sonde du 2026-08-31 : même HWND, même parent Win32, aucun `OnDisconnected`). `SessionsViewModel` reste propriétaire de toutes les sessions et gagne la notion d'emplacement ; une nouvelle `SessionWindow` héberge une session détachée ; `Core` gagne la géométrie mémorisée et le calcul de budget de fermeture, tous deux testables.

**Tech Stack:** .NET 10, WPF, WPF-UI 4.3.0, CommunityToolkit.Mvvm, xUnit.

**Spec:** `docs/superpowers/specs/2026-09-01-detached-windows-design.md` (et la spec produit `docs/superpowers/specs/2026-08-29-remotedeck-design.md` pour D12, §5, §6.5, §7.2/7.3).

## Global Constraints

- Baseline : `main` @ 8a4d968 (v0.1.0 publiée), 155 cas de test verts, build warning-free (`TreatWarningsAsErrors`).
- **Le détachement ne reconnecte jamais.** Aucune recréation de `RdpAxHost`, aucun secret re-présenté, aucun appel à `Connect()`. Si un déplacement provoque un `OnDisconnected`, c'est un échec de la tâche, pas un aléa.
- **Technique B uniquement** : `SessionsArea.Children.Remove(host)` puis affectation du **même** `host` au conteneur cible. Ni `SetParent` Win32 (détruit le HWND à la fermeture), ni nouveau `WindowsFormsHost` (laisse `RdpSession` mesurer la mauvaise fenêtre).
- `RdpSession` doit être averti d'un changement de fenêtre : réabonnement de `SizeChanged` et relecture du DPI. Sans cela la résolution dynamique (D6) vise l'ancienne fenêtre.
- Fermer une fenêtre détachée **déconnecte** la session par le protocole §6.5 (`RequestClose` → attente → repli `Disconnect()`). Fermer la fenêtre principale ferme l'application : `ShutdownMode.OnMainWindowClose`, budget **5 s par session, 30 s au total** (au lieu de 15 s).
- Aucune session sans propriétaire : un rattachement qui échoue laisse la session dans sa fenêtre avec un message dans l'`InfoBar`.
- Le hook clavier route vers la **fenêtre active** de l'application. Dans une fenêtre détachée : `Ctrl+W` ferme la session, `F11` bascule le plein écran, `Ctrl+K` ouvre la palette, `Ctrl+Shift+D` rattache ; `Ctrl+Tab` et `Ctrl+B` ne sont pas interceptés. La règle du lot 5 (ne pas manger les frappes des champs de saisie, sur le chemin du hook **et** des `KeyBinding`) reste inchangée.
- Jamais de `MessageBox`. Code, commentaires, UI et commits en **anglais** ; toute chaîne visible passe par `Strings.resx` **et** `Strings.fr.resx` (parité obligatoire, 198 clés aujourd'hui).
- `git add` par fichier, jamais `-A`/`.`. Commits : `git -c user.name="David Simon" -c user.email="david.simon@financieredelacite.com" commit -m "..."` + trailer `Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>`.
- TDD dans `RemoteDeck.Core`. `RemoteDeck.App` n'a pas de projet de tests : les tâches App se vérifient par build, lancement et check-list humaine.

---

## Carte des fichiers

| Fichier | Responsabilité |
|---|---|
| `src/RemoteDeck.Core/Settings/DetachedWindowPlacement.cs` | Géométrie mémorisée d'une fenêtre détachée (record) |
| `src/RemoteDeck.Core/Settings/AppSettings.cs` | + `DetachedWindows` (dictionnaire par connexion) |
| `src/RemoteDeck.Core/Sessions/ClosePlan.cs` | Répartition du budget de fermeture entre N sessions (pur) |
| `src/RemoteDeck.Core/Sessions/ScreenFit.cs` | Choix d'un placement valide sur les écrans disponibles (pur) |
| `tests/RemoteDeck.Core.Tests/Sessions/ClosePlanTests.cs`, `ScreenFitTests.cs`, `Settings/DetachedWindowPlacementTests.cs` | Tests |
| `src/RemoteDeck.App/Rdp/RdpSession.cs` | `AttachedTo(FrameworkElement)` : réabonnement + relecture DPI |
| `src/RemoteDeck.App/Rdp/RdpSessionHost.cs` | `EnableContainerHandledFullScreen()`, événements plein écran |
| `src/RemoteDeck.App/ViewModels/SessionTabViewModel.cs` | + `IsDetached` |
| `src/RemoteDeck.App/ViewModels/SessionsViewModel.cs` | Emplacement, `Detach`/`Reattach`, budget étendu |
| `src/RemoteDeck.App/Views/SessionWindow.xaml(.cs)` | Fenêtre d'une session détachée |
| `src/RemoteDeck.App/Views/SessionTabStrip.xaml.cs` | Glisser vertical → détachement |
| `src/RemoteDeck.App/Views/ShellWindow.xaml(.cs)` | Orchestration, palette, raccourcis, fermeture |
| `src/RemoteDeck.App/Rdp/ShortcutInterceptor.cs` | Routage vers la fenêtre active |
| `src/RemoteDeck.App/Resources/Strings.resx`, `Strings.fr.resx` | Nouvelles chaînes |
| docs | spec produit §7.2/§12, check-list, README, CHANGELOG |

---

### Task 1: `ClosePlan` + `ScreenFit` + `DetachedWindowPlacement` (Core, TDD)

**Files:**
- Create: `src/RemoteDeck.Core/Sessions/ClosePlan.cs`, `src/RemoteDeck.Core/Sessions/ScreenFit.cs`, `src/RemoteDeck.Core/Settings/DetachedWindowPlacement.cs`
- Modify: `src/RemoteDeck.Core/Settings/AppSettings.cs`
- Test: `tests/RemoteDeck.Core.Tests/Sessions/ClosePlanTests.cs`, `tests/RemoteDeck.Core.Tests/Sessions/ScreenFitTests.cs`, `tests/RemoteDeck.Core.Tests/Settings/DetachedWindowPlacementTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record DetachedWindowPlacement(double Left, double Top, double Width, double Height, bool FullScreen)`
  - `AppSettings.DetachedWindows` : `Dictionary<string, DetachedWindowPlacement>` (clé = `ConnectionId` en texte invariant ; `Dictionary` et non `long` car `System.Text.Json` sérialise mal les clés non-string), initialisé à vide, jamais null après `Load()`.
  - `public static class ClosePlan { const int PerSessionSeconds = 5; const int OverallSeconds = 30; static TimeSpan For(int remainingSessions, TimeSpan elapsed); }` — retourne le temps accordé à la prochaine fermeture : `min(5 s, budget restant)`, jamais négatif (`TimeSpan.Zero` quand le budget est épuisé).
  - `public readonly record struct ScreenBounds(double Left, double Top, double Width, double Height);`
  - `public static class ScreenFit { static DetachedWindowPlacement? Choose(DetachedWindowPlacement? saved, IReadOnlyList<ScreenBounds> screens, double minWidth = 640, double minHeight = 480); }` — `null` si `saved` est null ou si aucun écran ne l'accueille ; sinon un placement dont **au moins 120×40 pixels** du bandeau supérieur sont visibles sur un écran, ramené dans l'écran le plus recouvrant et borné aux tailles minimales.

- [ ] **Step 1: Écrire les tests**

`tests/RemoteDeck.Core.Tests/Sessions/ClosePlanTests.cs` :

```csharp
using RemoteDeck.Core.Sessions;

namespace RemoteDeck.Core.Tests.Sessions;

public sealed class ClosePlanTests
{
    [Fact]
    public void First_session_gets_the_per_session_budget()
        => Assert.Equal(TimeSpan.FromSeconds(5), ClosePlan.For(4, TimeSpan.Zero));

    [Fact]
    public void The_overall_budget_caps_the_last_sessions()
        => Assert.Equal(TimeSpan.FromSeconds(2), ClosePlan.For(3, TimeSpan.FromSeconds(28)));

    [Fact]
    public void An_exhausted_budget_gives_zero_not_a_negative_wait()
        => Assert.Equal(TimeSpan.Zero, ClosePlan.For(2, TimeSpan.FromSeconds(31)));

    [Fact]
    public void A_single_session_still_gets_five_seconds()
        => Assert.Equal(TimeSpan.FromSeconds(5), ClosePlan.For(1, TimeSpan.Zero));

    [Fact]
    public void Nothing_left_to_close_asks_for_nothing()
        => Assert.Equal(TimeSpan.Zero, ClosePlan.For(0, TimeSpan.Zero));
}
```

`tests/RemoteDeck.Core.Tests/Sessions/ScreenFitTests.cs` :

```csharp
using RemoteDeck.Core.Sessions;
using RemoteDeck.Core.Settings;

namespace RemoteDeck.Core.Tests.Sessions;

public sealed class ScreenFitTests
{
    private static readonly ScreenBounds Primary = new(0, 0, 1920, 1080);
    private static readonly ScreenBounds Secondary = new(1920, 0, 1920, 1080);
    private static readonly IReadOnlyList<ScreenBounds> Both = [Primary, Secondary];

    [Fact]
    public void No_saved_placement_means_no_suggestion()
        => Assert.Null(ScreenFit.Choose(null, Both));

    [Fact]
    public void A_placement_fully_on_a_screen_is_kept_as_is()
    {
        var saved = new DetachedWindowPlacement(2000, 100, 1280, 800, false);

        Assert.Equal(saved, ScreenFit.Choose(saved, Both));
    }

    [Fact]
    public void A_placement_on_a_screen_that_is_gone_is_dropped()
    {
        var saved = new DetachedWindowPlacement(2000, 100, 1280, 800, false);

        Assert.Null(ScreenFit.Choose(saved, [Primary]));
    }

    [Fact]
    public void A_barely_visible_placement_is_pulled_back_into_its_screen()
    {
        var saved = new DetachedWindowPlacement(1850, 100, 1280, 800, false);

        var fitted = ScreenFit.Choose(saved, [Primary]);

        Assert.NotNull(fitted);
        Assert.True(fitted.Left + fitted.Width <= Primary.Width);
        Assert.Equal(800, fitted.Height);
    }

    [Fact]
    public void A_window_larger_than_its_screen_is_shrunk_to_fit()
    {
        var saved = new DetachedWindowPlacement(0, 0, 3000, 2000, false);

        var fitted = ScreenFit.Choose(saved, [Primary]);

        Assert.NotNull(fitted);
        Assert.Equal(1920, fitted.Width);
        Assert.Equal(1080, fitted.Height);
    }

    [Fact]
    public void A_size_below_the_minimum_is_raised()
    {
        var saved = new DetachedWindowPlacement(10, 10, 100, 50, false);

        var fitted = ScreenFit.Choose(saved, [Primary]);

        Assert.NotNull(fitted);
        Assert.Equal(640, fitted.Width);
        Assert.Equal(480, fitted.Height);
    }

    [Fact]
    public void The_full_screen_flag_survives_fitting()
    {
        var saved = new DetachedWindowPlacement(1850, 100, 1280, 800, true);

        Assert.True(ScreenFit.Choose(saved, [Primary])!.FullScreen);
    }

    [Fact]
    public void No_screen_at_all_means_no_suggestion()
        => Assert.Null(ScreenFit.Choose(new DetachedWindowPlacement(0, 0, 800, 600, false), []));
}
```

`tests/RemoteDeck.Core.Tests/Settings/DetachedWindowPlacementTests.cs` :

```csharp
using RemoteDeck.Core.Settings;

namespace RemoteDeck.Core.Tests.Settings;

public sealed class DetachedWindowPlacementTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"remotedeck-detached-{Guid.NewGuid():N}", "settings.json");

    public void Dispose()
    {
        var dir = Path.GetDirectoryName(_path);
        if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void Detached_placements_survive_a_save_and_load()
    {
        var store = new SettingsStore(_path);
        var settings = store.Load();
        settings.DetachedWindows["42"] = new DetachedWindowPlacement(1920, 0, 1280, 800, true);

        store.Save(settings);
        var reloaded = store.Load();

        Assert.Single(reloaded.DetachedWindows);
        Assert.Equal(new DetachedWindowPlacement(1920, 0, 1280, 800, true), reloaded.DetachedWindows["42"]);
    }

    [Fact]
    public void A_settings_file_without_the_section_loads_with_an_empty_map()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, """{ "paneWidth": 320 }""");

        var settings = new SettingsStore(_path).Load();

        Assert.Equal(320, settings.PaneWidth);
        Assert.NotNull(settings.DetachedWindows);
        Assert.Empty(settings.DetachedWindows);
    }
}
```

- [ ] **Step 2: Vérifier que ça échoue**

Run: `dotnet test tests/RemoteDeck.Core.Tests`
Expected: échec de compilation — `ClosePlan`, `ScreenFit`, `DetachedWindowPlacement` n'existent pas.

- [ ] **Step 3: Implémenter**

`src/RemoteDeck.Core/Settings/DetachedWindowPlacement.cs` :

```csharp
namespace RemoteDeck.Core.Settings;

/// <summary>
/// Where a detached session window was last seen, in virtual-screen coordinates. Persisted per
/// connection in settings.json: reopening the same machine puts its window back where it was.
/// </summary>
public sealed record DetachedWindowPlacement(double Left, double Top, double Width, double Height, bool FullScreen);
```

Dans `src/RemoteDeck.Core/Settings/AppSettings.cs`, ajouter :

```csharp
    /// <summary>
    /// Geometry of each detached session window, keyed by connection id written as invariant text.
    /// A string key on purpose: System.Text.Json only round-trips dictionaries keyed by string
    /// without a converter. Never null after a Load().
    /// </summary>
    public Dictionary<string, DetachedWindowPlacement> DetachedWindows { get; set; } = [];
```

`src/RemoteDeck.Core/Sessions/ClosePlan.cs` :

```csharp
namespace RemoteDeck.Core.Sessions;

/// <summary>
/// How long the next session may take to close when the application is shutting down. Each session
/// gets five seconds, but the whole shutdown is capped: with detached windows the number of live
/// sessions is no longer bounded by what fits in a tab strip (design §6).
/// </summary>
public static class ClosePlan
{
    public const int PerSessionSeconds = 5;
    public const int OverallSeconds = 30;

    public static TimeSpan For(int remainingSessions, TimeSpan elapsed)
    {
        if (remainingSessions <= 0) return TimeSpan.Zero;

        var left = TimeSpan.FromSeconds(OverallSeconds) - elapsed;
        if (left <= TimeSpan.Zero) return TimeSpan.Zero;

        var perSession = TimeSpan.FromSeconds(PerSessionSeconds);
        return left < perSession ? left : perSession;
    }
}
```

`src/RemoteDeck.Core/Sessions/ScreenFit.cs` :

```csharp
using RemoteDeck.Core.Settings;

namespace RemoteDeck.Core.Sessions;

/// <summary>One screen's working area, in virtual-screen coordinates.</summary>
public readonly record struct ScreenBounds(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
}

/// <summary>
/// Turns a remembered placement into one that is usable on the screens present right now. A window
/// remembered on a monitor that has since been unplugged must not open off-screen where the user
/// cannot reach its title bar.
/// </summary>
public static class ScreenFit
{
    /// <summary>Minimum sliver of title bar that must land on a screen for a placement to be reachable.</summary>
    private const double VisibleWidth = 120;
    private const double VisibleHeight = 40;

    public static DetachedWindowPlacement? Choose(
        DetachedWindowPlacement? saved,
        IReadOnlyList<ScreenBounds> screens,
        double minWidth = 640,
        double minHeight = 480)
    {
        ArgumentNullException.ThrowIfNull(screens);
        if (saved is null || screens.Count == 0) return null;

        var screen = MostOverlapping(saved, screens);
        if (screen is null) return null;

        var bounds = screen.Value;
        double width = Math.Clamp(saved.Width, minWidth, bounds.Width);
        double height = Math.Clamp(saved.Height, minHeight, bounds.Height);
        double left = Math.Clamp(saved.Left, bounds.Left, bounds.Right - width);
        double top = Math.Clamp(saved.Top, bounds.Top, bounds.Bottom - height);

        return saved with { Left = left, Top = top, Width = width, Height = height };
    }

    /// <summary>The screen showing the most of the window's top strip, or null when none does.</summary>
    private static ScreenBounds? MostOverlapping(DetachedWindowPlacement saved, IReadOnlyList<ScreenBounds> screens)
    {
        ScreenBounds? best = null;
        double bestArea = 0;

        foreach (var screen in screens)
        {
            double overlapWidth = Math.Min(saved.Left + saved.Width, screen.Right) - Math.Max(saved.Left, screen.Left);
            double overlapHeight = Math.Min(saved.Top + VisibleHeight, screen.Bottom) - Math.Max(saved.Top, screen.Top);
            if (overlapWidth < VisibleWidth || overlapHeight < VisibleHeight) continue;

            double area = overlapWidth * overlapHeight;
            if (area <= bestArea) continue;

            bestArea = area;
            best = screen;
        }

        return best;
    }
}
```

- [ ] **Step 4: Vérifier que ça passe**

Run: `dotnet test RemoteDeck.sln`
Expected: 155 + 15 = 170 cas verts. Run: `dotnet build RemoteDeck.sln` → 0 avertissement.

- [ ] **Step 5: Commit**

```bash
git add src/RemoteDeck.Core/Settings/DetachedWindowPlacement.cs src/RemoteDeck.Core/Settings/AppSettings.cs src/RemoteDeck.Core/Sessions/ClosePlan.cs src/RemoteDeck.Core/Sessions/ScreenFit.cs tests/RemoteDeck.Core.Tests/Sessions/ClosePlanTests.cs tests/RemoteDeck.Core.Tests/Sessions/ScreenFitTests.cs tests/RemoteDeck.Core.Tests/Settings/DetachedWindowPlacementTests.cs
git commit -m "feat(core): shutdown budget, screen fitting and remembered geometry for detached windows"
```

---

### Task 2: `RdpSession.AttachedTo` et le plein écran géré par le conteneur

**Files:**
- Modify: `src/RemoteDeck.App/Rdp/RdpSession.cs`, `src/RemoteDeck.App/Rdp/RdpSessionHost.cs`

**Interfaces:**
- Consumes: rien de la tâche 1.
- Produces:
  - `RdpSession.AttachedTo(System.Windows.FrameworkElement newParent)` — à appeler **après** que `Host` a rejoint son nouveau conteneur : réabonne `SizeChanged`, relit le DPI, et déclenche une mise à jour d'affichage si la session est connectée et en mode `Dynamic`.
  - `RdpSession.FullScreenRequested` : `event Action<bool>?` (true = entrer, false = sortir), levé sur le thread UI depuis les événements du contrôle.
  - `RdpSessionHost.EnableContainerHandledFullScreen()` — pose `AdvancedSettings9.ContainerHandledFullScreen = true` ; retourne `bool` (false + `[display]` journalisé si la propriété est refusée, cf. risque R3).
  - `RdpSessionHost.RequestGoFullScreen` / `RequestLeaveFullScreen` : `event Action?`, levés depuis les sinks `OnRequestGoFullScreen` / `OnRequestLeaveFullScreen`, protégés par `Sink` comme tous les autres.

- [ ] **Step 1: Vérifier l'interop avant d'écrire**

Les événements `OnRequestGoFullScreen` / `OnRequestLeaveFullScreen` et la propriété `ContainerHandledFullScreen` doivent exister dans l'interop généré. Vérifier par réflexion, comme au lot 0 :

```powershell
$dll = Get-ChildItem -Recurse src/RemoteDeck.App/bin/Debug -Filter Interop.MSTSCLib.dll | Select-Object -First 1
$asm = [System.Reflection.Assembly]::LoadFile($dll.FullName)
$asm.GetType('MSTSCLib.IMsTscAxEvents_Event').GetEvents() | Where-Object Name -like '*FullScreen*' | ForEach-Object { $_.Name }
$asm.GetType('MSTSCLib.IMsRdpClientAdvancedSettings8').GetProperty('ContainerHandledFullScreen')
```

Consigner la sortie dans le rapport. Si `ContainerHandledFullScreen` est absent de `IMsRdpClientAdvancedSettings8`, il est sur `IMsTscAdvancedSettings` (`_client.AdvancedSettings`) — utiliser celle-là et le noter. Si les événements sont absents, **s'arrêter** et signaler : le repli R3 (plein écran WPF piloté par `F11` seul) devient la conception.

- [ ] **Step 2: `RdpSessionHost`**

Dans le constructeur, à côté des autres sinks :

```csharp
        // Container-handled full screen: Ctrl+Alt+Break stops toggling the control's own full-screen
        // window and raises these instead, so RemoteDeck keeps its own chrome (design §5).
        _events.OnRequestGoFullScreen += () => Sink("OnRequestGoFullScreen", () => RequestGoFullScreen?.Invoke());
        _events.OnRequestLeaveFullScreen += () => Sink("OnRequestLeaveFullScreen", () => RequestLeaveFullScreen?.Invoke());
```

et les membres :

```csharp
    /// <summary>Raised when the user asks for full screen (Ctrl+Alt+Break) and the container owns the switch.</summary>
    public event Action? RequestGoFullScreen;

    /// <summary>Raised when the user asks to leave full screen and the container owns the switch.</summary>
    public event Action? RequestLeaveFullScreen;

    /// <summary>
    /// Hands the full-screen switch to the container. The documented "no effect" caveat applies to the
    /// scripting-safe coclass only; RemoteDeck uses MsRdpClient12NotSafeForScripting.
    /// https://learn.microsoft.com/windows/win32/termserv/imstscadvancedsettings-containerhandledfullscreen
    /// </summary>
    public bool EnableContainerHandledFullScreen()
    {
        try
        {
            _client.AdvancedSettings9.ContainerHandledFullScreen = 1;
            return true;
        }
        catch (Exception ex)
        {
            ProbeLog.Write("display", $"ContainerHandledFullScreen refused: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            return false;
        }
    }
```

(Le type de la propriété est `int` dans l'interop — d'où `= 1` et non `= true`. Vérifier à l'étape 1 et adapter.)

Appeler `EnableContainerHandledFullScreen()` dans `Configure`, après `EnableAutoReconnect = false`.

- [ ] **Step 3: `RdpSession`**

Ajouter :

```csharp
    /// <summary>Raised when the hosting window should enter (true) or leave (false) full screen.</summary>
    public event Action<bool>? FullScreenRequested;

    /// <summary>
    /// Tells the session its host has moved to another window. The size subscription and the DPI both
    /// belong to the new parent: without this, dynamic resolution keeps measuring the old window —
    /// exactly the flaw the spike found on the alternative technique (design §2).
    /// </summary>
    public void AttachedTo(System.Windows.FrameworkElement newParent)
    {
        ArgumentNullException.ThrowIfNull(newParent);
        if (_disposed) return;

        Host.SizeChanged -= OnHostSizeChanged;
        Host.SizeChanged += OnHostSizeChanged;

        ProbeLog.Write("session", $"'{Connection.Name}': host attached to {newParent.GetType().Name}");

        if (State == SessionState.Connected && Connection.DisplayMode == DisplayMode.Dynamic)
        {
            _resizeTimer.Stop();
            _resizeTimer.Start();   // debounced: the new window may still be laying out
        }
    }
```

et, dans le constructeur de `RdpSessionHost` côté `RdpSession` (là où les autres événements sont branchés), relayer :

```csharp
        host.RequestGoFullScreen += () => Post(() => FullScreenRequested?.Invoke(true));
        host.RequestLeaveFullScreen += () => Post(() => FullScreenRequested?.Invoke(false));
```

- [ ] **Step 4: Build et lancement**

Run: `dotnet build RemoteDeck.sln` → 0 avertissement ; `dotnet test RemoteDeck.sln` → 170 verts ; lancer ~10 s, vérifier qu'aucune exception n'apparaît et que la ligne `ContainerHandledFullScreen refused` **n'apparaît pas** lors d'une connexion (si elle apparaît : R3 réalisé, le signaler).

- [ ] **Step 5: Commit**

```bash
git add src/RemoteDeck.App/Rdp/RdpSession.cs src/RemoteDeck.App/Rdp/RdpSessionHost.cs
git commit -m "feat(app): container-handled full screen and host re-parenting support"
```

---

### Task 3: `SessionWindow`

**Files:**
- Create: `src/RemoteDeck.App/Views/SessionWindow.xaml`, `src/RemoteDeck.App/Views/SessionWindow.xaml.cs`
- Modify: `src/RemoteDeck.App/Resources/Strings.resx`, `Strings.fr.resx`

**Interfaces:**
- Consumes: `SessionTabViewModel` (`Title`, `Subtitle`, `State`, `StatusBrushKey`, `CountdownText`), `RdpSession` (`Host`, `AttachedTo`, `FullScreenRequested`, `BuildDiagnostics`), `Controls/InfoBarExtensions.Show/Hide`.
- Produces:
  - `internal sealed partial class SessionWindow : Wpf.Ui.Controls.FluentWindow`
    - ctor `(SessionTabViewModel tab)`
    - `SessionTabViewModel Tab { get; }`
    - `System.Windows.Controls.Decorator HostArea { get; }` — le conteneur où le shell dépose le `WindowsFormsHost`
    - `event Action<SessionWindow>? ReattachRequested`
    - `event Action<SessionWindow>? CloseRequested` — levé sur clic de la croix ; le shell exécute le protocole puis appelle `AllowClose()`
    - `void AllowClose()` — autorise la fermeture réelle (le handler `Closing` annule tant qu'elle n'a pas été appelée)
    - `void ToggleFullScreen()` / `bool IsFullScreen { get; }`
    - `DetachedWindowPlacement CurrentPlacement()` — géométrie actuelle, y compris `FullScreen`

Détails : `WindowStyle="None"` en plein écran, `SingleBorderWindow` sinon ; `WindowBackdropType="Mica"` ; barre de titre = `Grid` de 32 px (pastille 8 px liée à `StatusBrushKey`, titre, sous-titre en 0.7 d'opacité, boutons *Reattach* / *Full screen* / *Close*), déplaçable par `DragMove()` sur double-clic maximisé exclu ; `InfoBar` en dessous ; `HostArea` = `Border` nommé `HostArea` occupant le reste. `F11` en `KeyBinding` → `ToggleFullScreen`. `Closing` : `e.Cancel = true` puis lever `CloseRequested` tant que `AllowClose()` n'a pas été appelée (même patron que le shell aujourd'hui).

Plein écran : mémoriser `WindowState`/`WindowStyle`/`RestoreBounds` avant de basculer, puis `WindowStyle = None; WindowState = Maximized;` — WPF maximise sur l'écran contenant la fenêtre. Sortir restaure les trois.

Nouvelles clés `Strings` (EN + FR) : `SessionWindow_Reattach` (« Reattach » / « Rattacher »), `SessionWindow_FullScreen` (« Full screen (F11) » / « Plein écran (F11) »), `SessionWindow_Close` (« Close session » / « Fermer la session »), `SessionWindow_Reconnect`, `SessionWindow_Cancel`, `SessionWindow_CopyDiagnostics` (mêmes libellés que dans le shell — réutiliser les clés existantes si elles existent déjà, ne pas dupliquer).

- [ ] Implémenter, build warning-free, `dotnet test` 170, lancer l'application (la fenêtre n'est pas encore atteignable — vérifier seulement qu'aucune régression n'apparaît), commit — `feat(app): window that hosts one detached session`.

---

### Task 4: Emplacement et déplacement dans `SessionsViewModel`

**Files:**
- Modify: `src/RemoteDeck.App/ViewModels/SessionsViewModel.cs`, `src/RemoteDeck.App/ViewModels/SessionTabViewModel.cs`

**Interfaces:**
- Consumes: `ClosePlan.For`, `SessionWindow`.
- Produces:
  - `SessionTabViewModel.IsDetached` : `bool` observable (la barre d'onglets masque les onglets détachés en liant `Visibility`).
  - `SessionsViewModel.Detach(SessionTabViewModel tab, SessionWindow window)` — retire le `Host` du conteneur ancré (via le `detach` déjà injecté au constructeur), le pose dans `window.HostArea.Child`, appelle `session.AttachedTo(window.HostArea)`, marque `IsDetached = true`, garde l'onglet dans `Tabs`, et active un voisin si l'onglet détaché était actif.
  - `SessionsViewModel.Reattach(SessionTabViewModel tab)` — l'inverse ; en cas d'échec (exception), laisse la session dans sa fenêtre, journalise `[session]` et retourne `false`.
  - `SessionsViewModel.DetachedWindowOf(SessionTabViewModel)` : `SessionWindow?`.
  - `CloseAllAsync` : la boucle utilise `ClosePlan.For(remaining, elapsed)` au lieu des constantes locales, et parcourt **tous** les onglets, détachés inclus ; chaque fenêtre détachée est fermée après le protocole.

Le constructeur reçoit déjà `Action<RdpSession> attach` / `detach` : les réutiliser pour le conteneur ancré, et ne pas les appeler pour le conteneur détaché (la fenêtre s'en charge).

- [ ] Implémenter, build, commit — `feat(app): session placement, detach and reattach`.

---

### Task 5: Le geste — glisser hors de la barre, rattacher par glisser

**Files:**
- Modify: `src/RemoteDeck.App/Views/SessionTabStrip.xaml.cs`, `src/RemoteDeck.App/Views/ShellWindow.xaml(.cs)`

**Interfaces:**
- Consumes: `SessionsViewModel.Detach/Reattach`, `SessionWindow`.
- Produces: `SessionTabStrip.DetachRequested` : `event Action<SessionTabViewModel, System.Windows.Point>?` (position écran du curseur au lâcher).

Dans `OnTabMouseMove`, le seuil horizontal existant (`DragThreshold = 4`) déclenche le réordonnancement ; ajouter : si `position.Y - _pressOrigin.Y > 40`, terminer le glisser et lever `DetachRequested` avec `PointToScreen`. Le shell crée la `SessionWindow`, la place (centrée sous le curseur, taille = celle de `SessionsArea`, ou géométrie mémorisée via `ScreenFit.Choose`), l'ouvre, puis appelle `Detach`.

Rattachement par glisser : la `SessionWindow` suit `LocationChanged` ; quand son coin supérieur gauche entre dans le rectangle écran de la barre d'onglets du shell (marge 24 px), afficher un liseré d'accueil sur la barre ; au relâchement du bouton (`Deactivated` ou `MouseLeftButtonUp` sur la barre de titre), appeler `Reattach`. **Simplification autorisée** si le suivi du glisser s'avère instable : ne garder que le bouton *Reattach*, la palette et `Ctrl+Shift+D`, et le noter dans le rapport.

- [ ] Implémenter, build, lancer, commit — `feat(app): drag a tab out to detach it, drag it back to reattach`.

---

### Task 6: Raccourcis, palette, fermeture de l'application, mémorisation

**Files:**
- Modify: `src/RemoteDeck.App/Rdp/ShortcutInterceptor.cs`, `src/RemoteDeck.App/Views/ShellWindow.xaml.cs`, `src/RemoteDeck.App/App.xaml.cs`, `src/RemoteDeck.App/Resources/Strings.resx`, `Strings.fr.resx`

**Interfaces:**
- Consumes: tout ce qui précède.
- Produces : routage clavier par fenêtre active ; entrées de palette ; `ShutdownMode.OnMainWindowClose` ; persistance.

Points :

1. **Routage** — `ShortcutInterceptor` ne connaît pas les fenêtres ; c'est le gestionnaire `Triggered` du shell qui doit router : `System.Windows.Application.Current.Windows` → la fenêtre `IsActive`. Si c'est une `SessionWindow` : `Ctrl+W` → fermer cette session, `F11` → `ToggleFullScreen`, `Ctrl+K` → palette avec `Owner` = cette fenêtre, `Ctrl+Shift+D` → `Reattach`, `Ctrl+Tab`/`Ctrl+B` → ignorés. Sinon, comportement actuel. Le prédicat `ShouldInterceptShortcut` du lot 5 reste consulté en premier.
2. **`Ctrl+Shift+D`** — ajouter `VkD = 0x44` dans `Decide` avec `IsDown(VkShift)` → `"Ctrl+Shift+D"`, et un `KeyBinding` équivalent sur les deux fenêtres.
3. **Palette** — deux entrées : `cmd:detach` (*Detach current session*, visible seulement si l'onglet actif est ancré) et `cmd:reattach` (visible seulement depuis une fenêtre détachée).
4. **Fermeture** — `App.xaml.cs` : `ShutdownMode = ShutdownMode.OnMainWindowClose`. Le `OnClosing` du shell appelle `CloseAllAsync(...)` qui couvre désormais les sessions détachées ; l'`InfoBar` annonce le nombre total.
5. **Mémorisation** — à la fermeture d'une `SessionWindow` et à la fermeture de l'application, écrire `settings.DetachedWindows[connectionId] = window.CurrentPlacement()` ; au détachement, lire `ScreenFit.Choose(settings.DetachedWindows.GetValueOrDefault(id), screens)` où `screens` vient de `SystemParameters.WorkArea` pour l'écran principal et de `System.Windows.Forms.Screen.AllScreens` pour les autres (WinForms est déjà référencé ; convertir en `ScreenBounds`).

- [ ] Implémenter, build, lancer, commit — `feat(app): window-aware shortcuts, palette entries, shutdown and remembered geometry`.

---

### Task 7: Docs

**Files:** spec produit (§7.2 disposition, §7.4 raccourcis, §12 nouvelle ligne « Fenêtres détachées »), `docs/manual-checklist.md` (section « Fenêtres détachées »), `README.md` (section *Sessions* : détacher, plein écran, deux écrans), `CHANGELOG.md` (section `## 0.2.0 — unreleased`).

- [ ] Commit — `docs: detached session windows`.

**Sonde humaine (fin de plan)** : détacher un onglet par glisser ; deuxième session détachée sur le second écran ; `F11` sur chacune → deux bureaux plein écran simultanés ; redimensionner une fenêtre détachée → la résolution distante suit ; `Ctrl+W` dans une fenêtre détachée → la session se ferme proprement ; rattacher par glisser et par `Ctrl+Shift+D` ; fermer l'application avec deux fenêtres détachées → `query session` : sessions **Disc**, aucun doublon ; rouvrir et détacher la même machine → la fenêtre revient au même endroit ; débrancher le second écran puis détacher → la fenêtre s'ouvre sur un écran existant.

---

## Auto-revue du plan

**Couverture de la spec** : §2 technique B (T4, contrainte globale) · §3 modèle et `SessionWindow` (T3, T4) · §4 gestes et mécanique, réabonnement DPI (T2, T4, T5) · §5 plein écran `ContainerHandledFullScreen` + repli R3 (T2, T3) · §6 fermeture, `ShutdownMode`, budget 30 s, aucune session orpheline (T1, T4, T6) · §7 clavier par fenêtre active (T6) · §8 mémorisation avec `ScreenFit` (T1, T6) · §9 hors périmètre : rien n'implémente `UseMultimon` · §10 risques : R1 (T2), R2 (T1/T6), R3 (T2 étape 1), R4 (T5 avec repli) · §11 tests : automatisables en T1, check-list humaine en T7.

**Cohérence des types** : `DetachedWindowPlacement(Left, Top, Width, Height, FullScreen)` produit en T1, consommé en T3 (`CurrentPlacement`) et T6 ; `ScreenFit.Choose(saved, screens, minWidth, minHeight)` en T1 → T6 ; `ClosePlan.For(remaining, elapsed)` en T1 → T4 ; `RdpSession.AttachedTo(FrameworkElement)` et `FullScreenRequested` en T2 → T3/T4 ; `SessionWindow.HostArea`/`AllowClose`/`ToggleFullScreen`/`CurrentPlacement` en T3 → T4/T5/T6 ; `SessionsViewModel.Detach/Reattach/DetachedWindowOf` en T4 → T5/T6 ; `SessionTabStrip.DetachRequested` en T5 → shell.

**Points d'attention pour l'exécutant** : l'étape 1 de la tâche 2 **conditionne** la conception du plein écran — si les événements manquent, s'arrêter et signaler ; `ContainerHandledFullScreen` est un `int` (`BOOL` COM), pas un `bool` ; les onglets détachés restent dans `Tabs` (ne pas « corriger » en les retirant, la palette et la fermeture globale s'appuient dessus) ; toute chaîne visible doit exister dans **les deux** `.resx` ; `Screen.AllScreens` vient de WinForms, déjà référencé, mais ne jamais ajouter `using System.Windows.Forms;` dans un fichier WPF — qualifier.
