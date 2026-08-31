# RemoteDeck — Lot 5 : palette de commandes, import `.rdp`, hook filtré, localisation

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fermer la v1 : palette de commandes `Ctrl+K` (connexions **et** commandes, correspondance floue, clavier seul), import des connexions existantes (`.rdp` et registre `mstsc`), hook clavier qui ne mange plus les frappes des champs de saisie, et interface entièrement localisable avec le français livré. À la fin, les cinq critères de succès du §1 sont vérifiables.

**Architecture:** `Core` gagne `Search/PaletteFilter` (items hétérogènes : connexions + commandes, réutilise `TextNormalizer`) et `Import/RdpFileImporter` + `Import/MstscRegistryImporter` (purs, testables — l'accès registre est injecté). `App` gagne `Views/CommandPaletteWindow` (fenêtre possédée, §7.3 airspace), `Views/ImportWindow` (prévisualisation + cases), `Resources/Strings.resx` + `Strings.fr.resx`, et `ShortcutInterceptor` apprend à ignorer les champs de saisie.

**Tech Stack:** .NET 10, WPF-UI 4.3.0, CommunityToolkit.Mvvm, `Microsoft.Win32.Registry` (BCL, Windows-only côté App), xUnit.

**Spec:** §7.3 (airspace, réserve 1 du hook), §7.4 (`Ctrl+K`), §7.5 (correspondance floue), §8 (import), §9 (localisation), §12 (L5), §1 (critères de succès). Baseline : `main` @ 4d4de34, 133 cas de test.

## Global Constraints

- **Clés `.rdp` vérifiées** (doc MS, 2026-08-31 — ne pas en inventer d'autres) : `full address:s:` (hôte, éventuellement `hôte:port`), `username:s:`, `domain:s:`, `desktopwidth:i:` / `desktopheight:i:` (200–8192), `desktop size id:i:` (0→640×480, 1→800×600, 2→1024×768, 3→1280×1024, 4→1600×1200), `screen mode id:i:` (1 fenêtré, 2 plein écran), `dynamic resolution:i:` (0 statique, 1 suit la fenêtre), `audiomode:i:` (0 local, 1 distant, 2 aucun), `redirectclipboard:i:` (0/1), `redirectprinters:i:` (0/1), `drivestoredirect:s:` (vide = aucun, `*` = tous, sinon liste), `authentication level:i:` (0 connecter sans avertir, 1 ne pas connecter, 2 avertir, **3 = non spécifié**), `enablerdsaadauth:i:` (0/1). Sources : <https://learn.microsoft.com/azure/virtual-desktop/rdp-properties>, <https://learn.microsoft.com/troubleshoot/windows-server/remote/remote-desktop-protocol-settings>.
  **Non vérifiées, donc ignorées à l'import** (comptées et journalisées, jamais devinées) : `server port:i:`, `administrative session:i:`, `connect to console:i:`, et toute autre clé.
- Import **non destructif** (§8) : prévisualisation, dédoublonnage sur `(Host, Port)` insensible à la casse contre la base **et** au sein du lot, cases à cocher, aucun mot de passe importé (le blob DPAPI d'un `.rdp` appartient à un autre contexte — ni lu, ni stocké, ni journalisé).
- Palette : fenêtre `FluentWindow` possédée (`Owner` = shell, `CenterOwner`, `ShowInTaskbar=false`, `WindowStyle=None`, `Topmost=false`) — jamais un overlay WPF au-dessus de la zone RDP (§7.3 airspace).
- Hook clavier (§7.3 réserve 1) : quand le focus clavier WPF est sur un champ de saisie (`TextBoxBase`, `PasswordBox`, `ComboBox { IsEditable = true }`), **`Ctrl+Tab`, `Ctrl+Shift+Tab`, `Ctrl+W` et `Ctrl+B` ne sont plus interceptés** ; `Ctrl+K` **reste** intercepté (c'est le seul point d'entrée de la palette et il n'a aucune sémantique dans un champ WPF). Aucune E/S synchrone ajoutée dans le callback : la décision reste locale (lecture de `Keyboard.FocusedElement`, appel géré, pas de fichier).
- Localisation (§9) : toutes les chaînes d'UI passent par `Strings.resx` (anglais neutre) + `Strings.fr.resx`. Les lignes de `ProbeLog` restent **en anglais, non localisées**. `NeutralResourcesLanguage("en")` sur l'assembly ; la culture suit `CultureInfo.CurrentUICulture` (donc Windows), sans réglage utilisateur en v1.
- Jamais de `MessageBox` ; anglais dans le code, les commentaires et les commits ; warning-free ; `git add` par fichier ; commits `git -c user.name="David Simon" -c user.email="david.simon@financieredelacite.com"` + trailer `Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>`.

---

## Carte des fichiers

| Fichier | Responsabilité |
|---|---|
| `src/RemoteDeck.Core/Search/PaletteFilter.cs` | Filtre flou sur des items hétérogènes + plages |
| `src/RemoteDeck.Core/Import/RdpFileImporter.cs` | Parse un `.rdp` → `ImportCandidate` |
| `src/RemoteDeck.Core/Import/MstscRegistryImporter.cs` | Hôtes du registre (lecture injectée) |
| `src/RemoteDeck.Core/Import/ImportCandidate.cs` | Résultat d'import + origine + avertissements |
| `tests/RemoteDeck.Core.Tests/Search/PaletteFilterTests.cs`, `Import/RdpFileImporterTests.cs`, `Import/MstscRegistryImporterTests.cs` | Tests |
| `src/RemoteDeck.App/ViewModels/CommandPaletteViewModel.cs` | Items, recherche, exécution |
| `src/RemoteDeck.App/Views/CommandPaletteWindow.xaml(.cs)` | Fenêtre `Ctrl+K` |
| `src/RemoteDeck.App/ViewModels/ImportViewModel.cs` | Sélection, dédoublonnage, application |
| `src/RemoteDeck.App/Views/ImportWindow.xaml(.cs)` | Prévisualisation et import |
| `src/RemoteDeck.App/Rdp/ShortcutInterceptor.cs` | Filtrage sur les champs de saisie |
| `src/RemoteDeck.App/Resources/Strings.resx`, `Strings.fr.resx` | Chaînes |
| Toutes les vues | Chaînes remplacées par des références de ressource |
| docs | spec §12 L5, check-list, README |

---

### Task 1: `PaletteFilter` (Core, TDD)

**Interfaces (produces):**
- `public enum PaletteItemKind { Connection, Command }`
- `public sealed record PaletteItem(PaletteItemKind Kind, string Id, string Title, string Subtitle, int Priority)` — `Id` = `conn:<id>` ou `cmd:<name>` ; `Priority` : bonus de tri (commandes fréquentes = plus haut).
- `public sealed record PaletteMatch(PaletteItem Item, int Score, IReadOnlyList<MatchRange> TitleRanges, IReadOnlyList<MatchRange> SubtitleRanges)`
- `public static class PaletteFilter { static IReadOnlyList<PaletteMatch> Apply(IEnumerable<PaletteItem> items, string? query, int limit = 50); }`

Règles : requête vide → tous les items, tri `Priority` décroissant puis `Title` (plié, ordinal) ; requête non vide → mêmes paliers que `ConnectionFilter` (préfixe du titre 100, sous-chaîne du titre 60, sous-chaîne du sous-titre 40, sous-séquence 10), chaque mot doit correspondre au titre **ou** au sous-titre, `+Priority` ajouté au score, tri score décroissant puis titre ; `limit` tronque après tri. Réutilise `TextNormalizer.Fold` (mêmes garanties de longueur, donc plages valides).

- [ ] **Step 1: Tests** — (1) requête vide : ordre priorité puis titre ; (2) `limit` respecté ; (3) préfixe du titre devant sous-chaîne du sous-titre ; (4) accents/casse ignorés ; (5) chaque mot doit correspondre (titre OU sous-titre) ; (6) plages du titre exactes sur une sous-chaîne ; (7) sous-séquence → une plage par caractère ; (8) `Priority` départage à score textuel égal ; (9) aucun résultat → liste vide.
- [ ] **Step 2: RED. Step 3: Implémenter. Step 4: GREEN** — 133 + 9 = 142 cas.
- [ ] **Step 5: Commit** — `feat(core): fuzzy palette filter over connections and commands`

---

### Task 2: `RdpFileImporter` + `MstscRegistryImporter` (Core, TDD)

**Interfaces (produces):**
- `public sealed record ImportCandidate { required string Name; required string Host; int Port = 3389; string? UserName; string? Domain; DisplayMode DisplayMode = DisplayMode.Dynamic; int? FixedWidth; int? FixedHeight; bool RedirectClipboard = true; bool RedirectDrives; bool RedirectPrinters; bool RedirectAudio; bool UseWebAccount; int? AuthenticationLevel; required string Source; IReadOnlyList<string> Warnings = []; }`
- `public static class RdpFileImporter`
  - `static ImportCandidate? Parse(string fileName, IEnumerable<string> lines)` — `null` si aucune `full address` exploitable. `Name` = nom de fichier sans extension. Lignes ignorées : vides, commentaires (`;`, `#`), mal formées (< 3 segments) — comptées dans `Warnings` (« 4 unsupported entries ignored »).
  - `static IReadOnlyList<ImportCandidate> ParseFolder(string folder, Func<string, IEnumerable<string>> readLines, IEnumerable<string> files)` — files fournis par l'appelant (testable sans disque).
  - Mapping **exact** (constantes du §Global) : `full address` → `Host` (+ `Port` si `host:port` numérique valide 1–65535, sinon avertissement et 3389) ; `username` → `UserName` ; `domain` → `Domain` ; `dynamic resolution:i:1` → `Dynamic` ; sinon si `desktopwidth`/`desktopheight` (200–8192) ou `desktop size id` 0–4 → `Scaled` + `FixedWidth/Height` ; `screen mode id` seul n'affecte pas le mode (plein écran ≠ résolution) mais est noté ; `audiomode` 0 → `RedirectAudio = true`, 1 ou 2 → `false` ; `redirectclipboard` → bool ; `redirectprinters` → bool ; `drivestoredirect` non vide → `RedirectDrives = true` ; `authentication level` 0/1/2 → valeur, **3 → null** ; `enablerdsaadauth:i:1` → `UseWebAccount = true`. Toute autre clé → comptée dans les avertissements, jamais devinée. Aucun mot de passe lu (`password 51:b:` est explicitement ignoré et **jamais** journalisé).
- `public static class MstscRegistryImporter { static IReadOnlyList<ImportCandidate> FromServers(IEnumerable<(string Host, string? UserName)> entries); }` — un candidat par hôte, `Name` = hôte, `Source` = `"mstsc registry"`. (La lecture réelle du registre vit dans l'App.)

- [ ] **Step 1: Tests** — `.rdp` complet réaliste (toutes les clés vérifiées) → tous les champs ; `full address:s:host:3390` → port 3390 ; `full address:s:host:abc` → 3389 + avertissement ; fichier sans `full address` → `null` ; clés inconnues comptées ; `password 51:b:` ignoré et absent des avertissements (pas de fuite) ; `authentication level:i:3` → null ; `desktop size id:i:2` → 1024×768 `Scaled` ; `dynamic resolution:i:1` gagne sur `desktopwidth` ; casse des clés ignorée (`Full Address:S:`) ; `ParseFolder` saute les fichiers illisibles (le `Func` lève) en les comptant ; registre : entrées dédoublonnées, hôte vide ignoré.
- [ ] **Step 2: RED. Step 3: Implémenter. Step 4: GREEN** — 142 + 13 = 155 cas.
- [ ] **Step 5: Commit** — `feat(core): .rdp file and mstsc registry importers`

---

### Task 3: Hook clavier — ne plus manger les frappes des champs de saisie

**Files:** Modify `src/RemoteDeck.App/Rdp/ShortcutInterceptor.cs`.

**Interfaces (produces):** `public Func<string, bool>? ShouldIntercept { get; set; }` — prédicat consulté **avant** de décider d'avaler un raccourci ; `null` = comportement actuel. Le shell l'installe : retourne `false` pour `Ctrl+Tab`/`Ctrl+Shift+Tab`/`Ctrl+W`/`Ctrl+B` quand `Keyboard.FocusedElement` est un `TextBoxBase`, un `PasswordBox` ou un `ComboBox { IsEditable: true }` ; `true` sinon (donc `Ctrl+K` toujours intercepté).

Le prédicat s'exécute dans le callback du hook : il ne doit faire que de la lecture d'état WPF (aucune E/S, aucun `Dispatcher.Invoke` synchrone — `Keyboard.FocusedElement` est lisible depuis le thread UI, qui est celui qui pompe le hook ; si le contrôle n'est pas sur ce thread, retourner `true` par défaut). Toute exception du prédicat → `true` (comportement d'avant) + `[shortcuts]` journalisé de façon différée.

- [ ] Implémenter, build, lancer, commit — `fix(app): let text inputs keep Ctrl+Tab, Ctrl+W and Ctrl+B`

---

### Task 4: Palette de commandes `Ctrl+K`

**Files:** Create `src/RemoteDeck.App/ViewModels/CommandPaletteViewModel.cs`, `src/RemoteDeck.App/Views/CommandPaletteWindow.xaml(.cs)` ; Modify `src/RemoteDeck.App/Views/ShellWindow.xaml.cs`.

**Interfaces (produces):**
- `CommandPaletteViewModel(IReadOnlyList<PaletteItem> items)` — `SearchText`, `ObservableCollection<PaletteMatch> Results` (recalcul **synchrone**, sans debounce : la liste est en mémoire et courte), `PaletteMatch? Selected`, `void MoveSelection(int delta)`.
- `CommandPaletteWindow(IReadOnlyList<PaletteItem> items)` — `string? ChosenId` après `ShowDialog()`.
- Le shell construit les items : une entrée par connexion (`Kind.Connection`, titre = nom, sous-titre = `groupe · hôte`, priorité 0) ; commandes (`Kind.Command`, priorité 10) : *New connection*, *Import connections…*, *Manage credentials…*, *Toggle connection pane*, *Close current tab*, *Reconnect current tab*, *Disconnect current tab*, plus une entrée par onglet ouvert (*Switch to <nom>*, priorité 5). Exécution : `conn:<id>` → même chemin que `Entrée` dans la liste ; `cmd:<name>` → l'action correspondante ; `tab:<index>` → activer.
- Fenêtre : largeur 560, `SizeToContent=Height` (max 420 avec `MaxHeight` + scroll), pas de barre de titre (`WindowStyle=None`, coin arrondi, ombre via `WindowBackdropType=Acrylic` si disponible sinon fond opaque), champ de recherche en haut avec `HighlightTextBlock` dans la liste, `↑`/`↓` naviguent, `Entrée` valide, `Échap` ferme, perte de focus (`Deactivated`) ferme. Ouverture centrée sur le shell.

- [ ] Implémenter, build, lancer, commit — `feat(app): Ctrl+K command palette over connections, commands and open tabs`

---

### Task 5: Import — fenêtre de prévisualisation et application

**Files:** Create `src/RemoteDeck.App/ViewModels/ImportViewModel.cs`, `src/RemoteDeck.App/Views/ImportWindow.xaml(.cs)` ; Modify `src/RemoteDeck.App/Views/ShellWindow.xaml.cs` (entrée de palette + bouton dans le panneau).

**Interfaces (produces):**
- `ImportViewModel(ConnectionRepository repository)` — `ObservableCollection<ImportRow> Rows` (`ImportRow { bool Selected; ImportCandidate Candidate; string Status }` où `Status` ∈ `"New"`, `"Duplicate of <name>"`, `"Already imported"`), `Task LoadFromFolderAsync(string folder)` (`Directory.EnumerateFiles(folder, "*.rdp", SearchOption.TopDirectoryOnly)` → `RdpFileImporter.ParseFolder`), `void LoadFromRegistry()` (lit `HKCU\Software\Microsoft\Terminal Server Client\Servers` : chaque sous-clé = hôte, valeur `UsernameHint` si présente → `MstscRegistryImporter.FromServers`), `int Import()` (insère les lignes cochées non dupliquées, retourne le nombre), `string Summary`.
- Dédoublonnage : `(Host, Port)` comparé sans casse à la base et au sein du lot ; une ligne dupliquée est décochée par défaut mais cochable (l'utilisateur décide).
- Fenêtre : `FluentWindow` possédée, deux boutons de source (*From a folder…* avec `OpenFolderDialog` de `Microsoft.Win32`, *From Remote Desktop Connection*), `ListView` avec case, nom, hôte:port, source, statut, avertissements en info-bulle ; boutons *Select all new* / *Clear* ; *Import N connection(s)* ; InfoBar de résultat. Aucun mot de passe, aucun identifiant créé — un message le dit explicitement.

- [ ] Implémenter, build, lancer, commit — `feat(app): import connections from .rdp files and the mstsc registry`

---

### Task 6: Localisation `.resx` + français

**Files:** Create `src/RemoteDeck.App/Resources/Strings.resx`, `Strings.fr.resx` ; Modify toutes les vues et view-models portant des chaînes visibles, `RemoteDeck.App.csproj` (génération du designer), `App.xaml.cs` (`NeutralResourcesLanguage`).

Règles : une clé par chaîne, nommée `Zone_Element` (ex. `Pane_SearchPlaceholder`, `Editor_Save`, `Session_Reconnecting`, `Import_Title`). Les chaînes composées passent par `string.Format` avec des marqueurs positionnels (`{0}`) — jamais de concaténation. Les messages de `DisconnectReason` (Core, anglais) **ne sont pas** localisés en v1 : ils sont affichés tels quels, précédés du titre localisé de la catégorie ; le noter dans la spec §9. Le français est une traduction complète du `.resx` neutre (aucune clé manquante — un test de parité serait idéal mais `RemoteDeck.App` n'a pas de projet de tests : vérifier par un script au build ? **Non** : comparer les deux fichiers à la main et le consigner dans le rapport).

- [ ] Implémenter par vue (shell, panneau, éditeurs, palette, import, onglets), build, lancer en `fr-FR` (`Start-Process` avec `$env:DOTNET_CLI_UI_LANGUAGE` n'agit pas sur `CurrentUICulture` : forcer temporairement dans `App.OnStartup` derrière une variable d'environnement `REMOTEDECK_UI_CULTURE` pour la vérification, en gardant le défaut système), commit — `feat(app): localizable UI with a complete French translation`

---

### Task 7: Docs et préparation de la v0.1.0

Spec §12 L5 « Fait » + §1 critères de succès annotés (lesquels sont vérifiés, par quoi) ; §7.3 réserve 1 levée ; §8 mapping réel des clés ; §9 état de la localisation ; §3 arbre. `docs/manual-checklist.md` section lot 5. README : palette, import, langue. `CHANGELOG.md` (nouveau) : v0.1.0 avec les lots 0→5.

- [ ] Commit — `docs: lot 5 status, changelog and usage`

**Sonde humaine (fin de lot)** : `Ctrl+K` → taper 2 lettres → `Entrée` connecte ; `Ctrl+K` → « imp » → *Import connections…* → dossier de `.rdp` → prévisualisation, cases, import → les connexions apparaissent ; dans la boîte de recherche, `Ctrl+W` **n'ferme plus** l'onglet ; interface en français au démarrage sur un Windows français ; les cinq critères du §1 vérifiés de bout en bout.

---

## Auto-revue du plan

**Couverture** : §7.4 `Ctrl+K` (T4) ; §7.5 flou réutilisé (T1) ; §7.3 réserve 1 (T3) ; §8 import complet, non destructif, sans mot de passe (T2, T5) ; §9 localisation (T6) ; §12 L5 (T7). Rien du lot n'est reporté.

**Types** : `PaletteItem`/`PaletteMatch` (T1) → VM/fenêtre (T4) ; `ImportCandidate` (T2) → `ImportRow`/VM (T5) ; `ShouldIntercept` (T3) installé par le shell (T3) ; les clés `.resx` traversent tout (T6, dernière pour ne pas conflicter).

**Points d'attention** : `OpenFolderDialog` existe dans `Microsoft.Win32` depuis .NET 8 (vérifier, sinon `System.Windows.Forms.FolderBrowserDialog`, déjà référencé) ; la palette ne doit pas être `Topmost` (elle est possédée) ; T6 touche tous les fichiers d'UI — la faire **après** T4/T5 pour ne pas réécrire deux fois ; le test de parité des `.resx` est manuel (aucun projet de tests App).
