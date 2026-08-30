# RemoteDeck — Lot 3 : panneau de connexions, recherche, favoris, éditeur

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remplacer la barre de sonde du lot 0 par la vraie UI de gestion : panneau gauche repliable listant les connexions par groupe (favoris épinglés), recherche floue avec surlignage, éditeur de connexion complet, navigation clavier intégrale — et connexion d'une entrée de la liste via le coffre. Une seule session à la fois (les onglets sont le lot 4).

**Architecture:** `Core` gagne `Search/ConnectionFilter` (flou, accents, plages de surlignage), `Settings/SettingsStore` (settings.json) et `Model/ConnectionRules` — tout testé. `App` gagne `ConnectionPane` (UserControl + `ConnectionListViewModel`), `ConnectionEditorWindow` (+ VM), un `ResourceDictionary` pour le `PasswordBox` natif, et un `ShellWindow` restructuré en deux colonnes avec `GridSplitter`. Le code-behind RDP existant (`RdpSessionHost`, `ShortcutInterceptor`, fermeture propre) est conservé tel quel ; seul l'assemblage change.

**Tech Stack:** .NET 10, WPF-UI 4.3.0, CommunityToolkit.Mvvm 8.4.2, `System.Text.Json` (BCL), xUnit.

**Spec:** `docs/superpowers/specs/2026-08-29-remotedeck-design.md` — §7 (7.1 à 7.5), §3, §10, §12 (L3), D12 (pas de `TabControl` — non concerné ici, une seule zone RDP), D6 (résolution dynamique = L4, **pas dans ce lot**).

## Global Constraints

- Baseline : `main` @ aeb679f, 47 tests verts. Branche `lot-3`.
- `RemoteDeck.Core` sans WPF/COM (System.Text.Json est BCL). TDD dans Core.
- Recherche (§7.5) : en mémoire, sur `Name`, `Host`, `GroupName`, insensible à la casse **et aux accents**, favoris d'abord, debounce 120 ms, plages de surlignage renvoyées par Core.
- Réglages d'affichage dans `%APPDATA%\RemoteDeck\settings.json` (§7.2), jamais dans SQLite.
- Raccourcis §7.4 de ce lot : `Ctrl+B` (panneau), `Ctrl+F` (recherche), `F2` (éditer), `Entrée` (connecter), `Ctrl+N` (nouvelle), `Suppr` (supprimer, deux temps). `Ctrl+K` / `Ctrl+Tab` restent en place (palette et onglets = lots 5/4). `Ctrl+B` s'ajoute au hook bas niveau pour marcher pendant que le contrôle RDP a le focus.
- Jamais de `MessageBox` ; erreurs et confirmations dans la fenêtre (InfoBar, bouton en deux temps).
- Fenêtres secondaires : `FluentWindow`, `Owner` = shell, `CenterOwner` (§7.3 airspace).
- Le chemin du secret ne change pas : `vault.UseSecret(credential, bstr => session.PutPassword(bstr))`. Aucun `string` de mot de passe.
- Le `PasswordBox` natif reste natif ; seul son style est repris (§7.1).
- Tout ce que le lot 0 a laissé « probe » dans `ShellWindow` (barre de saisie, `ManualEntry`, `REMOTEDECK_PROBE_*`) disparaît. `ProbeLog` reste (journal de diagnostic).
- Warning-free ; code/UI/commits en anglais ; `git add` par fichier, jamais `-A`/`.` ; jamais `.superpowers/`, `docs/PROJET.md`, `bin/`, `obj/`. Commits : `git -c user.name="David Simon" -c user.email="david.simon@financieredelacite.com" commit -m "..."` + trailer `Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>`.

---

## Carte des fichiers

| Fichier | Responsabilité |
|---|---|
| `src/RemoteDeck.Core/Search/ConnectionFilter.cs` | Correspondance floue, plages, tri (pur) |
| `src/RemoteDeck.Core/Search/TextNormalizer.cs` | Casse + accents (FormD, suppression des marques) |
| `src/RemoteDeck.Core/Settings/AppSettings.cs`, `SettingsStore.cs` | Réglages d'affichage, JSON, tolérant aux fichiers absents/corrompus |
| `src/RemoteDeck.Core/Model/ConnectionRules.cs` | Validation Name/Host/Port/Fixed size |
| `tests/RemoteDeck.Core.Tests/Search/ConnectionFilterTests.cs`, `Settings/SettingsStoreTests.cs`, `Model/ConnectionRulesTests.cs` | Tests |
| `src/RemoteDeck.App/Controls/InfoBarExtensions.cs` | `Show(severity, title, message)` partagé (dédoublonne `ShowStatus`) |
| `src/RemoteDeck.App/Controls/HighlightTextBlock.cs` | `TextBlock` avec `Text` + `Ranges` → runs surlignés |
| `src/RemoteDeck.App/Resources/PasswordBox.xaml` | Style Fluent du `PasswordBox` natif |
| `src/RemoteDeck.App/ViewModels/ConnectionListViewModel.cs` | Liste, recherche (debounce), sélection, groupes |
| `src/RemoteDeck.App/ViewModels/ConnectionEditorViewModel.cs` | Formulaire de connexion |
| `src/RemoteDeck.App/Views/ConnectionPane.xaml(.cs)` | Panneau gauche |
| `src/RemoteDeck.App/Views/ConnectionEditorWindow.xaml(.cs)` | Éditeur |
| `src/RemoteDeck.App/Views/ShellWindow.xaml(.cs)` | Deux colonnes, splitter, session unique, raccourcis, settings |
| `src/RemoteDeck.App/App.xaml` | Fusion du dictionnaire `PasswordBox.xaml` |
| `docs/…/spec §12`, `docs/manual-checklist.md`, `README.md` | Docs |

---

### Task 1: `TextNormalizer` + `ConnectionFilter` (Core, TDD)

**Files:** Create `src/RemoteDeck.Core/Search/TextNormalizer.cs`, `src/RemoteDeck.Core/Search/ConnectionFilter.cs` ; Test `tests/RemoteDeck.Core.Tests/Search/ConnectionFilterTests.cs`.

**Interfaces (produces):**
- `public static class TextNormalizer { static string Fold(string s); }` — minuscule invariante, accents retirés (`Normalize(FormD)` puis suppression des `UnicodeCategory.NonSpacingMark`), pour comparaison uniquement.
- `public readonly record struct MatchRange(int Start, int Length);`
- `public sealed record ConnectionMatch(Connection Connection, int Score, IReadOnlyList<MatchRange> NameRanges, IReadOnlyList<MatchRange> HostRanges);`
- `public static class ConnectionFilter { static IReadOnlyList<ConnectionMatch> Apply(IEnumerable<Connection> connections, string? query); }`

Règles : requête vide/blanche → toutes les connexions, `Score = 0`, favoris d'abord puis `GroupName` puis `Name` (ordinal insensible à la casse, sur texte plié). Requête non vide : pliée, découpée en mots (espaces) ; une connexion correspond si **chaque** mot est une **sous-séquence** (fuzzy) de `Name`, `Host` ou `GroupName` pliés. Score : +100 par mot trouvé en préfixe de `Name`, +60 en sous-chaîne contiguë de `Name`, +40 en sous-chaîne de `Host`, +20 en sous-chaîne de `GroupName`, +10 sous-séquence seulement ; +1000 si favori. Tri : score décroissant puis `Name`. Plages : positions (dans la chaîne **originale** — les index de la chaîne pliée sont identiques à ceux de l'originale car `Fold` conserve la longueur par caractère de base ; si `Normalize(FormD)` allonge la chaîne, calculer les plages sur une version pliée « même longueur » : construire le pli caractère par caractère en remplaçant chaque caractère par son premier caractère de base) ; renvoyer les plages des sous-chaînes contiguës trouvées (préfixe/sous-chaîne) ou, en sous-séquence, une plage par caractère apparié.

- [ ] **Step 1: Tests**

```csharp
using RemoteDeck.Core.Model;
using RemoteDeck.Core.Search;

namespace RemoteDeck.Core.Tests.Search;

public sealed class ConnectionFilterTests
{
    private static Connection C(string name, string host = "h", string group = "", bool fav = false)
        => new() { Name = name, Host = host, GroupName = group, IsFavorite = fav };

    [Fact]
    public void Fold_removes_case_and_accents_and_keeps_length()
    {
        Assert.Equal("elan", TextNormalizer.Fold("Élan"));
        Assert.Equal(4, TextNormalizer.Fold("Élan").Length);
        Assert.Equal("ss", TextNormalizer.Fold("SS"));
    }

    [Fact]
    public void Empty_query_returns_all_favorites_first_then_group_then_name()
    {
        var all = new[] { C("zeta", group: "Dev"), C("alpha", group: "Prod"), C("Beta", group: "Dev"), C("omega", group: "Prod", fav: true) };

        var names = ConnectionFilter.Apply(all, "  ").Select(m => m.Connection.Name).ToArray();

        Assert.Equal(new[] { "omega", "Beta", "zeta", "alpha" }, names);
    }

    [Fact]
    public void Query_is_accent_and_case_insensitive()
    {
        var all = new[] { C("Élan Prod"), C("Other") };

        var r = ConnectionFilter.Apply(all, "ELAN");

        Assert.Single(r);
        Assert.Equal("Élan Prod", r[0].Connection.Name);
        Assert.Equal(new MatchRange(0, 4), r[0].NameRanges[0]);
    }

    [Fact]
    public void Prefix_on_name_outranks_substring_on_host()
    {
        var all = new[] { C("Web01", host: "sql-prod"), C("SQL Prod", host: "web01") };

        var r = ConnectionFilter.Apply(all, "sql");

        Assert.Equal("SQL Prod", r[0].Connection.Name);
        Assert.Equal("Web01", r[1].Connection.Name);
        Assert.Equal(new MatchRange(0, 3), r[1].HostRanges[0]);
    }

    [Fact]
    public void Fuzzy_subsequence_matches_and_reports_each_character()
    {
        var all = new[] { C("Hyper-V Host 3") };

        var r = ConnectionFilter.Apply(all, "hvh");

        Assert.Single(r);
        Assert.Equal(3, r[0].NameRanges.Count);
        Assert.Equal(new MatchRange(0, 1), r[0].NameRanges[0]);
    }

    [Fact]
    public void Every_word_must_match_somewhere()
    {
        var all = new[] { C("DC01", host: "dc01.corp", group: "Prod"), C("DC02", host: "dc02.corp", group: "Dev") };

        var r = ConnectionFilter.Apply(all, "dc prod");

        Assert.Single(r);
        Assert.Equal("DC01", r[0].Connection.Name);
    }

    [Fact]
    public void Favorites_rank_first_even_with_lower_text_score()
    {
        var all = new[] { C("sql prefix"), C("x sql", fav: true) };

        var r = ConnectionFilter.Apply(all, "sql");

        Assert.Equal("x sql", r[0].Connection.Name);
    }

    [Fact]
    public void No_match_returns_empty()
        => Assert.Empty(ConnectionFilter.Apply([C("A")], "zzz"));
}
```

- [ ] **Step 2: RED** (compilation).
- [ ] **Step 3: Implémenter** — `TextNormalizer.Fold` : pour chaque caractère de la chaîne d'entrée, prendre `char.ToLowerInvariant` du **premier caractère non-marque** de sa décomposition `FormD` (ainsi la longueur est préservée caractère par caractère, et les plages restent valides sur l'original). `ConnectionFilter.Apply` : implémentation directe des règles ci-dessus (méthodes privées `FindPrefix`, `FindSubstring`, `FindSubsequence` renvoyant plages + score). Aucune dépendance.
- [ ] **Step 4: GREEN** — 55 tests (47 + 8). Warning-free.
- [ ] **Step 5: Commit** — `feat(core): fuzzy, accent-insensitive connection filter with highlight ranges`

---

### Task 2: `SettingsStore` + `ConnectionRules` (Core, TDD)

**Files:** Create `src/RemoteDeck.Core/Settings/AppSettings.cs`, `SettingsStore.cs`, `src/RemoteDeck.Core/Model/ConnectionRules.cs` ; Tests `tests/RemoteDeck.Core.Tests/Settings/SettingsStoreTests.cs`, `tests/RemoteDeck.Core.Tests/Model/ConnectionRulesTests.cs`.

**Interfaces (produces):**
- `public sealed class AppSettings { double PaneWidth = 300; bool PaneCollapsed; double? WindowLeft, WindowTop, WindowWidth, WindowHeight; bool WindowMaximized; long? LastConnectionId; }` (propriétés `{ get; set; }`).
- `public sealed class SettingsStore(string path)` — `static string DefaultPath()` → `%APPDATA%\RemoteDeck\settings.json` ; `AppSettings Load()` (fichier absent ou JSON invalide → défauts, jamais d'exception) ; `void Save(AppSettings)` (crée le répertoire, écriture atomique : fichier temporaire puis `File.Move(..., overwrite: true)`). `JsonSerializerOptions` : indenté, `PropertyNamingPolicy = CamelCase`.
- `public static class ConnectionRules { const int MaxNameLength = 80; static IReadOnlyList<string> Validate(string? name, string? host, int port, DisplayMode mode, int? fixedWidth, int? fixedHeight); }` — name requis ≤ 80 ; host requis (sans espaces) ; port 1–65535 ; si `mode != Dynamic`, `fixedWidth`/`fixedHeight` requis dans [640, 8192] / [480, 8192].

- [ ] **Step 1: Tests** — `SettingsStoreTests` : (1) `Load` sur fichier absent → défauts (`PaneWidth == 300`) ; (2) `Save` puis `Load` → mêmes valeurs (toutes les propriétés) ; (3) fichier contenant `{ not json` → défauts, pas d'exception ; (4) `Save` crée le répertoire parent. Utiliser un chemin `Path.GetTempPath()/remotedeck-settings-<guid>/settings.json`, nettoyé en `Dispose`. `ConnectionRulesTests` : (1) valide → vide ; (2) name/host requis → 2 erreurs ; (3) port 0 et 65536 → erreur ; (4) host avec espace → erreur ; (5) `Fixed` sans dimensions → erreur ; (6) `Dynamic` ignore les dimensions.
- [ ] **Step 2: RED. Step 3: Implémenter. Step 4: GREEN** — 65 tests (55 + 4 + 6).
- [ ] **Step 5: Commit** — `feat(core): settings store (settings.json) and connection validation rules`

---

### Task 3: `InfoBarExtensions`, `HighlightTextBlock`, style `PasswordBox`

**Files:** Create `src/RemoteDeck.App/Controls/InfoBarExtensions.cs`, `src/RemoteDeck.App/Controls/HighlightTextBlock.cs`, `src/RemoteDeck.App/Resources/PasswordBox.xaml` ; Modify `src/RemoteDeck.App/App.xaml`, `Views/CredentialsWindow.xaml.cs`, `Views/CredentialEditorWindow.xaml` (stretch), `Views/ShellWindow.xaml.cs` (utiliser l'extension).

**Interfaces (produces):**
- `internal static class InfoBarExtensions { static void Show(this Wpf.Ui.Controls.InfoBar bar, Wpf.Ui.Controls.InfoBarSeverity severity, string title, string message); static void Hide(this InfoBar bar); }`
- `public sealed class HighlightTextBlock : TextBlock` avec DP `Text` (string) et `Ranges` (`IReadOnlyList<MatchRange>?`) ; reconstruit `Inlines` : runs normaux + runs surlignés (`FontWeight.SemiBold` + `Foreground = SystemAccentColorBrush` de WPF-UI via `DynamicResource AccentTextFillColorPrimaryBrush`).
- `Resources/PasswordBox.xaml` : `Style TargetType="PasswordBox"` reproduisant l'apparence du `ui:TextBox` (fond `ControlFillColorDefaultBrush`, bordure `ControlElevationBorderBrush`, `CornerRadius 4`, `Padding 10,6`, `MinHeight 34`, états `IsMouseOver`/`IsKeyboardFocused` avec bordure d'accent en bas — ressources `DynamicResource` du thème WPF-UI). Fusionné dans `App.xaml` **après** `ControlsDictionary`.
- Éditeur d'identifiant : les `TextBox`/`PasswordBox` prennent toute la colonne (`HorizontalAlignment="Stretch"`), déjà le cas via `Grid` — vérifier ; ajouter `MinWidth`.

- [ ] Implémenter, remplacer les deux `ShowStatus` privés par `StatusBar.Show(...)`, build, lancer (la fenêtre *Credentials → Add* montre un `PasswordBox` stylé Fluent avec placeholder), WM_CLOSE. Commit — `feat(app): shared InfoBar helper, highlight text block, Fluent style for the native PasswordBox`.

---

### Task 4: `ConnectionListViewModel` + `ConnectionPane`

**Files:** Create `src/RemoteDeck.App/ViewModels/ConnectionListViewModel.cs`, `src/RemoteDeck.App/Views/ConnectionPane.xaml(.cs)`.

**Interfaces (produces):**
- `ConnectionListViewModel : ObservableObject` — ctor `(ConnectionRepository repository)` ; `ObservableCollection<ConnectionMatch> Items` ; `string SearchText` (déclenche un `DispatcherTimer` 120 ms → `Refresh()`) ; `ConnectionMatch? Selected` ; `void Reload()` (relit la base, réapplique le filtre) ; `void Refresh()` (filtre en mémoire, via `ConnectionFilter.Apply`) ; `string GroupOf(ConnectionMatch m)` = `"★ Favorites"` si favori sinon `GroupName` ou `"Ungrouped"` ; événements `ConnectRequested(Connection)`, `EditRequested(Connection?)` (null = nouvelle), `DeleteRequested(Connection)`.
- `ConnectionPane : UserControl` — `ui:TextBox` de recherche (`PlaceholderText="Search  (Ctrl+F)"`, icône loupe), `ListView` virtualisé (`VirtualizingPanel.IsVirtualizing`, `ScrollUnit=Pixel`) lié à une `CollectionViewSource` groupée sur `GroupOf` avec `GroupStyle` (en-tête collant : `ScrollViewer.CanContentScroll="False"` + en-tête en `Border` semi-transparent ; l'effet « sticky » strict n'est pas requis par le plan si le coût est déraisonnable — noter le choix), `ItemTemplate` : 32 px, `HighlightTextBlock` pour `Name` (ranges `NameRanges`) + `HighlightTextBlock` secondaire (`Host`, opacité 0.7, `HostRanges`), glyphe `` (étoile, Segoe Fluent Icons) si favori. Touches dans la liste : `Entrée` → `ConnectRequested`, `F2` → `EditRequested(selected)`, `Suppr` → `DeleteRequested`, `Ctrl+N` → `EditRequested(null)` ; double-clic → connecter. Bouton « + New » en tête. État vide : texte centré « No connections yet — press Ctrl+N to add one. ». Méthode publique `FocusSearch()`.

- [ ] Implémenter, build, commit — `feat(app): connection pane with grouped, searchable, keyboard-driven list`.

---

### Task 5: `ConnectionEditorViewModel` + `ConnectionEditorWindow`

**Files:** Create `src/RemoteDeck.App/ViewModels/ConnectionEditorViewModel.cs`, `src/RemoteDeck.App/Views/ConnectionEditorWindow.xaml(.cs)`.

**Interfaces (produces):**
- VM : propriétés observables pour chaque champ de `Connection` (`Name`, `Host`, `Port`, `GroupName`, `CredentialId` (via `SelectedCredential` : `Credential?`), `IsFavorite`, `DisplayMode`, `FixedWidth`, `FixedHeight`, 4 redirections, `AdminSession`, `UseWebAccount`, `AuthenticationLevel` (`int?` : null = default, 0/1/2), `Notes`), `IReadOnlyList<Credential> Credentials` (+ entrée « (none) » = null), `IReadOnlyList<string> KnownGroups` ; `string Errors` ; `bool Validate()` (→ `ConnectionRules`) ; `void ApplyTo(Connection)` / `static ConnectionEditorViewModel From(Connection?, credentials, groups)`.
- Fenêtre : `FluentWindow`, `Width 520`, `SizeToContent Height`, `CenterOwner`, sections : *General* (Name *, Host *, Port, Group = `ComboBox IsEditable` avec `KnownGroups`, Favorite), *Sign-in* (Credential combo, Use web account, Authentication level combo « Default / No server auth / Required / Prompt if failed » (valeurs null/0/1/2 — **valeurs vérifiées au lot 0**)), *Display* (Display mode combo Dynamic/Scaled/Fixed, W/H `ui:NumberBox` activés si ≠ Dynamic), *Redirections* (4 cases), *Advanced* (Admin session, Notes multiligne). InfoBar d'erreurs. `bool Saved`. Sauvegarde : `Insert`/`Update` via `ConnectionRepository`, exceptions dans l'InfoBar, log `[connections] '<name>' created/updated`.

- [ ] Implémenter, build, commit — `feat(app): connection editor window`.

---

### Task 6: `ShellWindow` restructuré (deux colonnes, session unique, raccourcis, settings)

**Files:** Modify `src/RemoteDeck.App/Views/ShellWindow.xaml(.cs)`, `src/RemoteDeck.App/Rdp/ShortcutInterceptor.cs` (ajout `Ctrl+B`), `src/RemoteDeck.App/Rdp/RdpConnectionProbeSettings.cs` → renommer `RdpConnectionSettings` (record étendu : `Host, Port, UserName, Domain, UseWebAccount, AdminSession, RedirectClipboard, RedirectDrives, RedirectPrinters, RedirectAudio, AuthenticationLevel (int?), DisplayMode, FixedWidth, FixedHeight`) et `RdpSessionHost.Configure` (applique toutes ces valeurs : `advanced.ConnectToAdministerServer`, `RedirectDrives/Printers`, `AudioRedirectionMode` via `SecuredSettings2.AudioRedirectionMode` (**0 = lire localement, 2 = ne pas lire** — vérifier sur la page MS `IMsRdpClientSecuredSettings::AudioRedirectionMode` avant d'écrire, citer l'URL en commentaire), `AuthenticationLevel` si non null, `SmartSizing` = `mode == Scaled`, `DesktopWidth/Height` = fixe si `Fixed` sinon taille de l'hôte).

**Layout XAML :**
```
Grid (colonnes : Pane [largeur settings, min 220] | GridSplitter 4 px | RDP *)
  Ligne 0 (ColumnSpan 3) : ui:TitleBar
  Col 0 : views:ConnectionPane x:Name="Pane"
  Col 2 : Grid lignes Auto/Auto/* :
     - barre de session : TextBlock (nom + hôte de la session courante ou « No session »), ui:Button Disconnect (activé si connecté), ToggleButton « ☰ » (Ctrl+B)
     - InfoBar StatusBar
     - Border noir + WindowsFormsHost RdpHost
```
Repli : `Ctrl+B` met la colonne 0 à largeur 0 (et cache le splitter) ; mémorise `PaneWidth`/`PaneCollapsed` dans `AppSettings` (sauvegarde à la fermeture et au relâchement du splitter). Position/taille/état de la fenêtre restaurés au démarrage (si l'écran les contient) et sauvegardés à la fermeture.

**Flux de connexion :** `Pane.ViewModel.ConnectRequested += Connect` → si une session est connectée : `await _session.CloseAsync(5 s)` puis reconfigurer ; construire `RdpConnectionSettings` depuis la `Connection` ; si `CredentialId` non null : charger l'identifiant, `UserName`/`Domain` depuis lui, `vault.UseSecret` ; sinon (aucun identifiant) : `EnableCredSspSupport` reste vrai et le contrôle affiche sa propre invite d'identifiants (chemin manuel supprimé — la barre de sonde disparaît) ; `Connect()` ; `repository.TouchLastConnected(id)` ; InfoBar. Les événements de session existants (`StatusChanged`, `Disconnected` avec codes 0–3 informatifs) sont conservés. `Edit`/`New` → `ConnectionEditorWindow` puis `Pane.ViewModel.Reload()` ; `Delete` → deux temps dans l'InfoBar du shell (« Delete '<name>'? Press Delete again to confirm » pendant 5 s).

**Raccourcis :** `KeyBinding`s WPF sur la fenêtre pour `Ctrl+F` (`Pane.FocusSearch()`), `Ctrl+N`, `Ctrl+B` ; le `ShortcutInterceptor` gagne `Ctrl+B` (VK 0x42) → `Triggered("Ctrl+B")` → repli du panneau, pour que ça marche aussi quand le contrôle RDP a le focus. `Ctrl+K`/`Ctrl+Tab` : InfoBar « arrives in lot 5/4 » inchangée.

**Suppression de la sonde :** `HostInput`… `WebAccountInput`, `ManualEntry`, `ReloadCredentials`, `OnCredentialChanged`, `REMOTEDECK_PROBE_HOST/USER/DOMAIN` disparaissent ; le bouton *Credentials…* migre dans la barre de session (icône clé) ; `REMOTEDECK_PROBE_SHORTCUTS` reste (diagnostic, §7.3).

- [ ] Implémenter, build, lancer : au premier lancement la liste est vide avec l'état vide ; `Ctrl+N` ouvre l'éditeur ; WM_CLOSE. Commit — `feat(app): two-column shell with connection pane, single-session area, shortcuts and persisted layout`.

**Sonde humaine (fin de lot, §12 L3 « navigation clavier intégrale ») :** `Ctrl+N` → créer une connexion vers la VM de test avec l'identifiant du coffre → `Entrée` dans la liste → *Logged on* ; `Ctrl+F` → taper 2 lettres → surlignage ; `F2` → éditer ; `Ctrl+B` pendant que la session a le focus → panneau replié ; fermer/rouvrir → largeur, état du panneau et fenêtre restaurés ; `Suppr` deux fois → suppression.

---

### Task 7: Docs

**Files:** spec §12 (L3 « Fait »), §3 (fichiers `Search/`, `Settings/`, `Controls/`, `ConnectionPane`), `docs/manual-checklist.md` (section « Lot 3 »), `README.md` (section *Usage* : raccourcis §7.4 disponibles, premier lancement).

- [ ] Commit — `docs: lot 3 status, manual checklist and usage`.

---

## Auto-revue du plan

**Couverture §7** : 7.2 deux colonnes + splitter + repli + settings.json (T2, T6) ; 7.4 raccourcis du lot (T4, T6) ; 7.5 recherche floue accents/casse/favoris/debounce/plages (T1, T4) ; 7.1 style `PasswordBox` + stretch (T3) ; liste virtualisée, groupes, 32 px, étoile, état vide (T4) ; éditeur complet (T5) ; InfoBar partagée (T3). **Hors lot** (assumé) : onglets/D12 et résolution dynamique/D6 (L4), palette (L5), reconnexion (L4), import (L5), `.resx` (L5).

**Types** : `ConnectionMatch`/`MatchRange` (T1) consommés par `HighlightTextBlock` (T3) et le VM/pane (T4) ; `AppSettings`/`SettingsStore` (T2) par le shell (T6) ; `ConnectionRules` (T2) par l'éditeur VM (T5) ; `RdpConnectionSettings` (T6) construit depuis `Connection` (modèle L1) ; `InfoBarExtensions.Show` remplace les deux `ShowStatus`.

**Points d'attention** : `AudioRedirectionMode` — valeurs à **vérifier sur la page MS** avant usage (règle « jamais supposer ») ; `ConnectToAdministerServer` est sur `IMsRdpClientAdvancedSettings6` (hérité par `AdvancedSettings9`) ; le `GroupStyle` sticky peut être simplifié ; `CollectionViewSource` + `ObservableCollection` : rafraîchir la vue après `Refresh()` ; le hook LL + `Ctrl+B` : ajouter le VK dans `Decide`.
