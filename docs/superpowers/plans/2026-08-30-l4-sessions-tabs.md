# RemoteDeck — Lot 4 : onglets multi-sessions, reconnexion, résolution dynamique

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Plusieurs sessions RDP simultanées en onglets (D12), navigation `Ctrl+Tab` / `Ctrl+Shift+Tab` / `Ctrl+W`, reconnexion automatique avec backoff sur coupure réseau (jamais sur refus d'identifiants), fermeture propre de **tous** les onglets à la sortie, résolution dynamique au redimensionnement (D6), et messages de déconnexion explicites issus de la table Microsoft.

**Architecture:** `Core` gagne `Sessions/ReconnectPolicy` et `Diagnostics/DisconnectReason` (purs, TDD). `App` gagne `Rdp/RdpSession` (machine à états par onglet, boucle de reconnexion, redimensionnement débouncé), `ViewModels/SessionTabViewModel` + `SessionsViewModel`, `Views/SessionTabStrip` (barre d'onglets custom) et une zone où **tous** les `WindowsFormsHost` restent instanciés (`Visible`/`Hidden`). `RdpSessionHost` (lot 0–3) est conservé comme façade COM d'une session ; il gagne `UpdateDisplay`. Le shell orchestre : connexion depuis la liste = nouvel onglet (ou activation de l'onglet existant pour la même connexion).

**Tech Stack:** .NET 10, WPF-UI 4.3.0, CommunityToolkit.Mvvm, xUnit.

**Spec:** §6.2 (machine à états), §6.3 (reconnexion), §6.4 (erreurs explicites), §6.5 (fermeture propre), §7.1/7.2 (onglets, D12), D6, §12 (L4). Baseline : `main` @ 7219cdd, 68 tests.

## Global Constraints

- **Valeurs vérifiées (doc MS, 2026-08-30)** — ne pas en inventer d'autres :
  - `IMsRdpClient9::UpdateSessionDisplaySettings(ULONG ulDesktopWidth, ULONG ulDesktopHeight, ULONG ulPhysicalWidth, ULONG ulPhysicalHeight, ULONG ulOrientation, ULONG ulDesktopScaleFactor, ULONG ulDeviceScaleFactor)` → `HRESULT`. Source : <https://learn.microsoft.com/en-us/previous-versions/windows/desktop/legacy/mt703457(v=vs.85)>. Le contrôle sélectionné (v12) implémente `IMsRdpClient10` ⊃ `IMsRdpClient9`.
  - Codes `OnDisconnected` (table complète, `IMsTscAxEvents::OnDisconnected`) : non-erreurs **0** NoInfo, **1** LocalNotError, **2** RemoteByUser, **3** ByServer ; réseau : **260** DNSLookupFailed, **264** ConnectionTimedOut, **516** SocketConnectFailed, **520** HostNotFound, **772** WinsockSendFailed, **776** InvalidIPAddr, **1028** SocketRecvFailed, **1288** DNSLookupFailed2, **1540** GetHostByNameFailed, **1796** TimeoutOccurred, **2052** InvalidIP, **2308** AtClientWinsockFDCLOSE ; mémoire : 262, 518, 774 ; interne/timer : 1032, 1544 ; sécurité/chiffrement : 1030, 1286, 1542, 1798, 2310, 2566, 2822, 3078, 3080 ; licence : 2056, 2312 ; authentification (`SSL_ERR_*`) : 2055 LogonFailure, 2567 NoSuchUser, 2823 AccountDisabled, 3079 AccountRestriction, 3335 AccountLockedOut, 3591 AccountExpired, 3847 PasswordExpired, 4615 PasswordMustChange, 5639 DelegationPolicy, 5895 PolicyNtlmOnly, 6151 NoAuthenticatingAuthority, 6919 CertExpired, 7175 SmartcardWrongPin, 8455 FreshCredRequiredByServer, 8711 SmartcardCardBlocked. Source : <https://learn.microsoft.com/windows/win32/termserv/imstscaxevents-ondisconnected>.
  - `ControlCloseStatus` 0/1 ; `RequestClose()` retourne la valeur (lot 0).
- Reconnexion (§6.3) : **uniquement** sur `ReconnectPolicy.ShouldReconnect(reason)` = codes réseau **transitoires** {264, 516, 772, 1028, 1796, 2308} (perte de socket / timeout). **Jamais** sur 0–3, jamais sur DNS/IP invalide (260, 520, 776, 1288, 1540, 2052 — l'hôte ne reviendra pas en 60 s), jamais sur `SSL_ERR_*`, jamais sur sécurité/licence. Délais **2, 5, 10, 30, 60 s**, 5 tentatives, puis `Failed` avec bouton *Reconnect*. Annulable. Le secret est **re-fourni à chaque tentative** via le coffre (la session porte `CredentialId`, jamais le secret).
- Codes 0–3 : jamais présentés comme des erreurs ; `GetErrorDescription` non affiché pour eux (§6.4, lot 0).
- Onglets (D12) : pas de `TabControl` ; tous les `WindowsFormsHost` vivent dans un `Grid`, actif = `Visible`, autres = `Hidden`.
- Résolution dynamique (D6) : `SizeChanged` de la zone → debounce **300 ms** → `UpdateDisplay` ; échec (HRESULT non S_OK ou exception) → repli `SmartSizing = true` pour cette session, journalisé une fois. Uniquement pour `DisplayMode.Dynamic`.
- Fermeture : `Ctrl+W` et croix d'onglet → `CloseAsync` (protocole §6.5) ; fermeture de la fenêtre → `CloseAsync` de **chaque** onglet (séquentiel, 5 s chacun, plafond global 15 s puis `Disconnect()` forcé), fenêtre gardée interactive avec InfoBar « Closing N sessions… ».
- Aucune E/S synchrone dans le callback du hook LL (`Ctrl+W` = VK 0x57 s'ajoute à `Decide`).
- Jamais de `MessageBox` ; anglais ; warning-free ; `git add` par fichier ; commits `git -c user.name="David Simon" -c user.email="david.simon@financieredelacite.com"` + trailer `Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>`.

---

## Carte des fichiers

| Fichier | Responsabilité |
|---|---|
| `src/RemoteDeck.Core/Sessions/ReconnectPolicy.cs` | Décision + délais de reconnexion |
| `src/RemoteDeck.Core/Diagnostics/DisconnectReason.cs` | Table codes → catégorie + message |
| `tests/RemoteDeck.Core.Tests/Sessions/ReconnectPolicyTests.cs`, `Diagnostics/DisconnectReasonTests.cs` | Tests |
| `src/RemoteDeck.App/Rdp/RdpSessionHost.cs` | + `UpdateDisplay`, + `Reason`/`Extended` dans `Disconnected` (déjà), `Configure` ré-appelable |
| `src/RemoteDeck.App/Rdp/RdpSession.cs` | Machine à états, reconnexion, resize, un `RdpAxHost` + un `WindowsFormsHost` par session |
| `src/RemoteDeck.App/Rdp/SessionState.cs` | Énumération d'état |
| `src/RemoteDeck.App/ViewModels/SessionTabViewModel.cs`, `SessionsViewModel.cs` | Onglet (titre, état, couleur), collection + actif + navigation |
| `src/RemoteDeck.App/Views/SessionTabStrip.xaml(.cs)` | Barre d'onglets (34 px, pastille, croix, réordonnancement) |
| `src/RemoteDeck.App/Views/ShellWindow.xaml(.cs)` | Zone de sessions, orchestration, fermeture de tous les onglets |
| `src/RemoteDeck.App/Rdp/ShortcutInterceptor.cs` | + `Ctrl+W` |
| docs | spec §12 L4, check-list, README |

---

### Task 1: `ReconnectPolicy` + `DisconnectReason` (Core, TDD)

**Interfaces (produces):**
- `public static class ReconnectPolicy { static IReadOnlyList<TimeSpan> Delays { get; } /* 2,5,10,30,60 s */ ; const int MaxAttempts = 5; static bool ShouldReconnect(int reason); static TimeSpan? DelayFor(int attempt); /* attempt 1..5 → Delays[attempt-1], sinon null */ }`
- `public enum DisconnectCategory { NotAnError, Network, Authentication, Security, Licensing, Resources, Internal, Unknown }`
- `public sealed record DisconnectDescription(int Reason, DisconnectCategory Category, string Title, bool IsError);`
- `public static class DisconnectReason { static DisconnectDescription Describe(int reason); }` — table **complète** ci-dessus, `Title` court en anglais (ex. 264 → "Connection timed out", 2055 → "Logon failed", 3 → "Disconnected by the server") ; code inconnu → `Unknown`, `Title = $"Disconnected (code {reason})"`, `IsError = true`. `IsError = false` pour 0–3.

- [ ] **Step 1: Tests** — `ReconnectPolicyTests` : (1) délais exacts `[2,5,10,30,60]` s ; (2) `DelayFor(1)==2s`, `DelayFor(5)==60s`, `DelayFor(6)==null`, `DelayFor(0)==null` ; (3) `ShouldReconnect` vrai pour 264, 516, 772, 1028, 1796, 2308 ; (4) faux pour 0,1,2,3 ; (5) faux pour 2055, 3335, 8455 (auth) ; (6) faux pour 260, 520, 2052 (DNS/IP) ; (7) faux pour 1286, 2056 (sécurité/licence). `DisconnectReasonTests` : (1) 0–3 → `NotAnError`, `IsError == false` ; (2) 264 → Network ; (3) 2055 → Authentication ; (4) 3078 → Security ; (5) 2312 → Licensing ; (6) 518 → Resources ; (7) 1032 → Internal ; (8) 424242 → Unknown, titre contient "424242" ; (9) chaque code de la table a un `Title` non vide (test paramétré sur la liste des 47 codes).
- [ ] **Step 2: RED. Step 3: Implémenter** (dictionnaire statique, URL MS en commentaire d'en-tête). **Step 4: GREEN** — 68 + 16 = 84.
- [ ] **Step 5: Commit** — `feat(core): reconnect policy and disconnect reason table`

---

### Task 2: `RdpSessionHost` — `UpdateDisplay`, configuration ré-appelable

**Files:** Modify `src/RemoteDeck.App/Rdp/RdpSessionHost.cs`.

**Interfaces (produces):**
- `public bool UpdateDisplay(int width, int height, uint scalePercent)` — appelle `((IMsRdpClient9)ocx).UpdateSessionDisplaySettings((uint)width, (uint)height, (uint)physW, (uint)physH, 0, scalePercent, scalePercent)` où `physW/H` = mm approximatifs (`width * 254 / (96 * scale/100) / 10` — ou 0 si l'interop l'accepte ; documenter le choix) ; retourne `false` (et journalise `[display]` une fois) si l'appel lève ou si le contrôle n'est pas connecté. Vérifier la signature générée par TlbImp (`uint` × 7) avant d'écrire.
- `public void EnableSmartSizingFallback()` — `AdvancedSettings9.SmartSizing = true` (modifiable connecté, §2).
- `Configure` doit pouvoir être rappelé entre deux tentatives (déjà le cas : il ne fait que poser des propriétés ; vérifier qu'aucune n'est interdite en état déconnecté — `KeyboardHookMode`/`AudioRedirectionMode` ne peuvent être posés que déconnecté, ce qui est le cas au moment de la reconnexion).
- `Disconnected` reste `Action<RdpDisconnectInfo>` ; ajouter `event Action? Connected` distinct de `StatusChanged` (pour la machine à états).

- [ ] Implémenter, build, commit — `feat(app): dynamic display update and reconnect-friendly session host`

---

### Task 3: `RdpSession` (machine à états, reconnexion, resize) + `SessionState`

**Files:** Create `src/RemoteDeck.App/Rdp/SessionState.cs`, `src/RemoteDeck.App/Rdp/RdpSession.cs`.

**Interfaces (produces):**
- `public enum SessionState { Idle, Connecting, Connected, Interrupted, Reconnecting, Failed, Closing, Closed }`
- `public sealed class RdpSession : IDisposable`
  - ctor `(Connection connection, RdpControlVersion version, Func<RdpSessionHost, Task> supplyAndConnect)` — le délégué (fourni par le shell) configure les settings, prête le secret via le coffre et appelle `Connect()` ; il est ré-invoqué à chaque tentative.
  - `Connection Connection`, `WindowsFormsHost Host` (créé avec un `RdpAxHost` enfant), `SessionState State`, `int Attempt`, `TimeSpan? NextRetryIn`, `DisconnectDescription? LastDisconnect`, `string? LastWindowsDescription`.
  - `event Action? Changed` (état/compte à rebours), `Task StartAsync()`, `Task ReconnectNowAsync()` (annule le timer, tente immédiatement, remet `Attempt` à 0 si l'utilisateur le demande depuis `Failed`), `void CancelReconnect()` → `Failed`, `Task CloseAsync(TimeSpan timeout)` (→ `Closing`, protocole §6.5 via `RdpSessionHost.CloseAsync`, puis `Closed` + dispose de l'hôte), `string BuildDiagnostics()` (texte : connexion, hôte, code, extended, titre, description Windows, tentatives — pour *Copy diagnostics*).
  - Transitions : `StartAsync` → Connecting ; `Connected` → Connected (Attempt=0) ; `Disconnected(info)` : si `State == Closing` → Closed ; sinon si `ReconnectPolicy.ShouldReconnect(info.Reason)` et `Attempt < MaxAttempts` → Interrupted → planifie `DelayFor(++Attempt)` (DispatcherTimer, `NextRetryIn` décrémenté chaque seconde pour l'UI) → Reconnecting → `supplyAndConnect` ; sinon → Failed (ou Idle si 0–3 : « disconnected normally », l'onglet reste ouvert avec un bouton *Reconnect*).
  - Resize : s'abonne à `Host.SizeChanged` ; si `Connection.DisplayMode == Dynamic` et `State == Connected` : debounce 300 ms → `UpdateDisplay(w, h, dpi)` ; au premier `false` → `EnableSmartSizingFallback()` + `[display] fallback to SmartSizing` et arrêt des tentatives pour cette session.
  - Tous les rappels COM restent dans `RdpSessionHost` (déjà `Sink`-protégés) ; `RdpSession` ne touche pas au COM directement.

- [ ] Implémenter, build, commit — `feat(app): per-tab RDP session with state machine, backoff reconnection and dynamic resolution`

---

### Task 4: Onglets — `SessionTabViewModel`, `SessionsViewModel`, `SessionTabStrip`, intégration shell, `Ctrl+W`

**Files:** Create `src/RemoteDeck.App/ViewModels/SessionTabViewModel.cs`, `SessionsViewModel.cs`, `src/RemoteDeck.App/Views/SessionTabStrip.xaml(.cs)` ; Modify `src/RemoteDeck.App/Views/ShellWindow.xaml(.cs)`, `src/RemoteDeck.App/Rdp/ShortcutInterceptor.cs`.

**Interfaces (produces):**
- `SessionTabViewModel(RdpSession session)` : `Title` (nom de la connexion), `Subtitle` (hôte), `State`, `StatusBrushKey` (vert `SystemFillColorSuccessBrush` connecté / ambre `SystemFillColorCautionBrush` Interrupted-Reconnecting / rouge `SystemFillColorCriticalBrush` Failed / gris sinon), `CountdownText` (« retry in 7 s »), `CloseCommand`.
- `SessionsViewModel` : `ObservableCollection<SessionTabViewModel> Tabs`, `Active`, `Next()`, `Previous()`, `Move(from, to)`, `SessionTabViewModel Open(RdpSession)`, `Task CloseAsync(tab)`, `Task CloseAllAsync(TimeSpan perTab, TimeSpan overall)`, `SessionTabViewModel? Find(long connectionId)`.
- `SessionTabStrip : UserControl` — `ItemsControl` horizontal, `ItemsSource = Tabs`, onglet 34 px, coins arrondis 6, pastille 8 px, titre, croix au survol/actif, clic = activer, clic-milieu = fermer, **réordonnancement au glisser** (drag avec seuil 4 px, drop entre onglets → `Move`), état actif = fond `ControlFillColorDefaultBrush` + bordure basse accent. Animation 150 ms d'opacité sur activation (§7.1).
- Shell : la zone RDP devient `Grid x:Name="SessionsArea"` ; `Open` ajoute `session.Host` à `SessionsArea.Children` ; activation = `Visibility.Visible` pour l'actif, `Hidden` pour les autres. Connexion depuis la liste : si `Find(connection.Id)` existe → activer ; sinon `new RdpSession(connection, version, SupplyAndConnect)` → `Open` → `StartAsync`. `SupplyAndConnect` = code actuel d'`OnConnectRequested` (settings, identifiant, `UseSecret`, `Connect`, `TouchLastConnected`) déplacé dans une méthode réutilisable. Barre de session par onglet actif : titre, état, boutons *Reconnect* (visible en Failed/Idle), *Cancel* (en Interrupted/Reconnecting), *Copy diagnostics*, *Disconnect*, *Credentials…*. InfoBar : les codes non-erreur en `Informational`, réseau en `Warning` (avec compte à rebours), auth/sécurité en `Error` + description Windows. `Ctrl+Tab`/`Ctrl+Shift+Tab` → `Next/Previous` (le hook LL le remonte déjà : remplacer l'InfoBar placeholder) ; `Ctrl+W` → `CloseAsync(Active)` (hook LL + `KeyBinding`). Fermeture de la fenêtre → `CloseAllAsync(5 s, 15 s)` avec InfoBar « Closing N sessions… », puis fermeture (garde de réentrance existante conservée). Suppression d'une connexion qui a un onglet ouvert : l'onglet est fermé d'abord.
- `ShortcutInterceptor.Decide` : `VK_W (0x57)` avec Ctrl → `"Ctrl+W"`.

- [ ] Implémenter, build, lancer (aucune session : zone vide avec texte « Select a connection and press Enter »), WM_CLOSE. Commit — `feat(app): session tabs with Ctrl+Tab navigation, close protocol for every tab, reconnect UI`.

**Sonde humaine (fin de lot, §12 L4)** : ouvrir 2 sessions (2 connexions vers la même VM ou 2 VM) → 2 onglets, `Ctrl+Tab` bascule sans coupure ; redimensionner la fenêtre → le bureau distant **change de résolution** (net, pas d'étirement) ; **couper le réseau de la VM** (désactiver la carte réseau dans Hyper-V ou `Disable-NetAdapter` dans la VM) → onglet ambre « retry in … » → réactiver avant 60 s → onglet vert, session reprise ; recouper et laisser expirer → rouge *Failed*, bouton *Reconnect* → reprise ; `Ctrl+W` ferme proprement ; fermer la fenêtre avec 2 sessions ouvertes → `query session` : sessions **Disc**, pas de doublon.

---

### Task 5: Docs

Spec §12 L4 « Fait » (+ ce qui reste en L5), §3 arbre (`Sessions/`, `Diagnostics/`, `RdpSession`, `SessionTabStrip`), §6.2 (états réels), §6.3 (ensemble exact des codes de reconnexion), `docs/manual-checklist.md` section lot 4, README (multi-sessions, `Ctrl+Tab`/`Ctrl+W`, reconnexion).

- [ ] Commit — `docs: lot 4 status, checklist and usage`

---

## Auto-revue du plan

**Couverture spec** : §6.2 (T3), §6.3 y compris « jamais sur refus d'identifiants » et re-fourniture du secret (T1, T3, T4), §6.4 catégories + actions InfoBar (T1, T4), §6.5 pour chaque onglet + sortie (T4), D12 (T4), D6 avec repli SmartSizing (T2, T3), §7.1 onglets 34 px/pastille/réordonnancement/animation (T4), `Ctrl+Tab`/`Ctrl+W` (T4). Hors lot : palette, import, `.resx`, filtrage du hook sur les champs (L5).

**Types** : `ReconnectPolicy.ShouldReconnect/DelayFor` et `DisconnectReason.Describe` (T1) consommés par `RdpSession` (T3) et l'InfoBar (T4) ; `RdpSessionHost.UpdateDisplay/EnableSmartSizingFallback/Connected` (T2) par `RdpSession` (T3) ; `RdpSession` API (T3) par les VMs/strip/shell (T4).

**Points d'attention** : signature TlbImp d'`UpdateSessionDisplaySettings` (7 × `uint`) à confirmer ; `Hidden` (pas `Collapsed`) pour les hôtes inactifs ; le hook LL ne doit pas déclencher `Ctrl+W` dans les `TextBox` (limitation connue → L5) ; `DispatcherTimer` des reconnexions arrêtés en `Dispose` ; DPI : `VisualTreeHelper.GetDpi(host)` → `scalePercent = (uint)Math.Round(DpiScaleX * 100)`.
