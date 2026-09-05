# Full pass — security, performance, visual polish

2026-09-05, on `main` at `0e10a18` (0.4.0 merged, not tagged). Read-only: every finding below was
checked against the source, the built output or the live probe log, and each names its evidence.
Nothing was changed. A findings list that names only faults misleads, so what is *right* is
recorded too — it is what a later change must not break.

## Security

### S1 — The server-authentication level is whatever the control decides
`RdpSessionHost.cs:160-165` sets `AuthenticationLevel` only when the connection carries a value.
`Connection.AuthenticationLevel` defaults to `null`, and the editor's first option is "leave the
client default". Microsoft documents the three values (0 connect silently, 1 refuse, 2 warn) and
**no default** for the ActiveX control. So a freshly created connection's protection against a
spoofed server is a value nobody in this repository chose or measured.

*Fix:* make 2 (`mstsc`'s own `authentication level:i:2`) RemoteDeck's explicit default when the
connection says nothing, keep the editor override, and read the value back from the control once
in a probe so the checklist records what "client default" actually was. Small.

### S2 — `AcceptedCertThumbprint` is a column nothing reads
Present since schema V1 (`SchemaMigrator.cs`), carried through the repository, never read or
written by the App (grep: one comment in `ConnectionEditorWindow.xaml.cs:58`). The R5 probe
`LogCertificateSurface` (`RdpSessionHost.cs:309`) is the reason: the TlbImp interop exposes no
thumbprint API, so trust-on-first-use pinning was investigated and found unreachable. The schema
still promises it.

*Fix:* say so in `SECURITY.md` under *what this does NOT cover* — the control's own certificate
dialog is the only server-identity check — and drop the column in a later schema. Small.

### S3 — No unhandled-exception handler anywhere
Grep for `DispatcherUnhandledException`, `UnhandledException`, `UnobservedTaskException`: nothing.
`OnConnectRequested` (`ShellWindow.xaml.cs:1746`) is `async void`; an exception after its first
`await` ends the process with no line in the probe log. The same holds for every event handler.

*Fix:* register the three handlers in `App`, log type, HResult, message and stack, and let the
process die — never swallow. A crash that leaves evidence is a bug that gets fixed. Small.

### S4 — The probe log is unbounded, opened per line, and identifying
`ProbeLog.cs:23` — `File.AppendAllText` under a lock, on the calling (usually UI) thread, one
open/close per line. Measured on the reference client: **586 KB, 1,245 lines** already; 607 of
them `[session]`; 41 lines name a user, UPN or domain; 3 name a host. No rotation, no cap.
`SECURITY.md` says logs hold "credentials by label and user name only" — true, and incomplete:
they also hold host names and connection names.

*Fix:* one `StreamWriter` with `AutoFlush`, roll at ~1 MB keeping one predecessor, and one honest
sentence in `SECURITY.md`. Small.

### S5 — Manifest hygiene
`app.manifest` declares no `requestedExecutionLevel`. `asInvoker` is the explicit statement that
the application never wants elevation and opts out of UAC virtualisation; x64 processes are not
subject to installer detection, so this is hygiene rather than exposure. Its `assemblyIdentity`
version is frozen at `0.1.0.0`. Trivial.

### Informational
Single-file publish self-extracts the native libraries to `%TEMP%\.net\RemoteDeck\<hash>` — a
user-writable directory, standard .NET behaviour, low. Not worth a change on its own.

### What is right, and must stay so
- `dotnet list package --vulnerable --include-transitive`: **no vulnerable package** in any project.
- The vault (`Core/Security/`): DPAPI `CurrentUser`, 32 bytes of fresh entropy per save, the secret
  never a managed string, every intermediate buffer zeroed, the BSTR lent and `ZeroFreeBSTR`'d.
- The password reaches the control by a raw vtable call (`ComSecretPut.cs`) — the generated
  `set_ClearTextPassword(string)` is deliberately bypassed.
- The low-level keyboard hook (`ShortcutInterceptor.cs:166-201`) acts only when the foreground
  window belongs to this process, reads nothing but `vkCode`, and posts every side effect off the
  300 ms budget. It never logs a key.
- `.rdp` import discards `password 51:b` (`RdpFileImporter.cs:74`); the registry importer reads
  HKCU only; SQL is parameterised; SQLite runs WAL with foreign keys on.
- `EnableCredSspSupport = true` (NLA) unconditionally.

## Performance

### O1 — Thirteen languages of satellite assemblies in a two-language application
Measured in `bin/Release/net10.0-windows/win-x64/`: `cs de es fr it ja ko pl pt-BR ru tr zh-Hans
zh-Hant`, **18 MB** of `*.resources.dll`, all bundled into the 175 MB single file. RemoteDeck ships
English and French. `<SatelliteResourceLanguages>en;fr</SatelliteResourceLanguages>` in
`Directory.Build.props` removes ~16 MB from the executable and from what it maps at start. Trivial,
and the release dry-run proves it.

### O2 — ReadyToRun and compression: measure, then decide
`PublishReadyToRun` is off; a WPF + WinForms + WPF-UI process JIT-compiles a lot before its first
window. R2R usually cuts cold start noticeably at the cost of a larger file;
`EnableCompressionInSingleFile` does the reverse. Neither number is known for this application, and
the probe log already timestamps "RemoteDeck starting" — add one line at first window shown and
measure three cold starts each way before choosing. Medium.

### O3 — The connection list is rebuilt one item at a time
`ConnectionListViewModel.cs:231-232`: `Items.Clear()` then `Add` per item, each raising
`CollectionChanged` and re-running the grouped `CollectionViewSource`. `ConnectionFilter` re-folds
name, host and group of every connection on every keystroke (`TextNormalizer.Fold`, FormD per
non-ASCII character). Invisible at 50 connections, visible at 500. Wrap the rebuild in
`DeferRefresh()` and cache the folded fields on the item. Small; only matters if the list grows.

### O4 — The keyboard hook has no liveness check
`ShortcutInterceptor.cs:367` records that Windows silently removes a `WH_KEYBOARD_LL` hook that
exceeds `LowLevelHooksTimeout` (300 ms). The side effects are correctly deferred, but nothing
notices a hook Windows has already removed, and there is no API to ask. After one UI-thread stall —
a GC pause, a slow disk under `AppendAllText`, anything — every application shortcut is dead for
the rest of the session, silently. Re-arm on the shell's `Activated` (unhook, hook again): cheap,
and it turns a permanent failure into one that lasts until the next click. Small, real.

### What is right
Both lists virtualise with recycling and `IsVirtualizingWhenGrouping`; search is debounced at
120 ms; `RdpSession.Dispose` (`:679-716`) stops both timers, unhooks every handler and detaches the
host before disposing the control; no `ProbeLog.Write` sits in a resize or keystroke path; the
database opens once at startup and migrations are cheap.

## Visual polish

### V1 — Almost no motion, and what there is has no token
Corrected after the first version of this review claimed "zero Storyboard": there are three, and
they are good — the row hover in both lists and on the session tabs fades over 150 ms, declarative,
with the reason written beside each (selection stays instant so a keyboard user never waits to see
where they are). All three wrote the number as a literal, `0:0:0.15`, in a sheet that keeps closed
sets for every other metric — and a grep cut short by `head` is how the first version missed them.

Everything else appears and vanishes in one frame: the palette — Acrylic, round corners — pops into
existence and pops out; the InfoBar appears; the active tab snaps; the full-screen bar toggles. A
150 ms opacity-and-6-px arrival on the palette and the InfoBar, an 80 ms departure, and a tab
indicator that slides are the changes that would most alter how the application *feels*, and they
cost nothing at runtime. The durations belong in the sheet, as three tokens, and Windows'
"Animation effects" setting must turn all of them to zero. Medium.

### V2 — Type is not a token
`Theme.xaml` keeps closed sets for radii and heights and says a value outside them is a defect;
type has no such set. Fifteen literal `FontSize` values across the views (12 ×7, 11 ×5, 14 ×3).
Add `RdTextSm` / `RdText` / `RdTextLg` (and a weight or two) to the sheet, in the same spirit.
Small.

### V3 — Two literal colours
`ShellWindow.xaml:147` and `SessionWindow.xaml:198`: `#FF000000` for the ground behind the remote
desktop. The sheet's own first sentence is "colours are never literal here". A `RdSessionGround`
token. Trivial.

### V4 — Accessibility is the blind spot
`AutomationProperties`: 0. Custom `FocusVisualStyle`: 0. High-contrast handling: 0. Roughly 18
icon buttons, about half with a tooltip. The status pill already pairs colour with a word (good).
Name every icon-only button and the pill for screen readers, and look at focus rings on the dark
theme by hand. Small to medium.

### What is right
Mica on the shell, the session windows and every editor; Acrylic on the palette; every brush
derived from WPF-UI's theme colours through `DynamicResource`, so accent and light/dark follow
Windows live — `SystemThemeWatcher.Watch` on every window; PerMonitorV2 DPI; 23 `SymbolIcon`s; 40
tooltips; empty-state messages in both languages.

### Caveat
This is a reading of the XAML. The application was not launched — the reference client may be
running the 0.4.0-rc.1 binary against the same database. A screenshot pass (light and dark, 100 %
and 150 %) is the second half of this section and should follow when the machine is free.

## Proposed order

Interest = risk removed × inverse of cost.

| # | Item | Why first | Cost |
|---|------|-----------|------|
| 1 | S1 authentication level | The only finding that can change what a spoofed server gets | small |
| 2 | O1 satellite languages | −16 MB for one line, proven by the dry-run | trivial |
| 3 | S3 exception logging | Every future crash becomes diagnosable | small |
| 4 | O4 hook re-arm | Removes a silent, permanent failure | small |
| 5 | S4 log rotation | Bounded disk, one honest sentence in SECURITY.md | small |
| 6 | V1 + V2 + V3 | The visual pass, one commit: motion, type tokens, ground token | medium |
| 7 | V4 accessibility names | Cheap, and the kind of thing a first external user notices | small |
| 8 | S2 dead column | Document now, drop in V5 | small |
| 9 | O2 R2R / compression | Measure three cold starts first | medium |
| 10 | O3 list rebuild | Only when the list grows | small |
| 11 | S5 manifest | Hygiene | trivial |

## Ergonomics — the application driven, 2026-09-06

The 0.4.1-rc.4 build launched and driven by keyboard and mouse, fourteen screenshots: first
screen, palette open and filtered, credentials, import, editor empty and with validation errors,
search with no match, row selected, both context menus, delete armed and expired, pane folded.
Sessions, tabs, detaching and full screen were not exercised — they need a server — and are
reviewed from the README's promises only.

### What holds
Every shortcut in the README's table did what the table says. `Delete` arms, says so in the
InfoBar with the connection's name, and the arming expires on its own after five seconds. The
row menu carries the four actions with their keys; the empty-pane menu carries the two that need
no row. Search with no match says so, with the query quoted. `Ctrl+B` folds and unfolds, and the
empty state re-centres. Escape closes the editor; Enter saves and, on an empty form, lists every
reason at once instead of the first.

### What does not — defects
- **E1 — Validation messages are English in a French interface.** `Vérifiez le formulaire —
  Name is required. Host is required.` `ConnectionRules.Validate` and `CredentialRules.Validate`
  return eight English sentences from `Core`, and the view models show them as they are. Core
  should return codes; the App owns the words. Medium: two rule classes, their tests, keys ×2.
- **E2 — Two windows have white lists in the dark theme.** *Manage credentials* and *Import
  connections* use a plain WPF `ListView` with a `GridView`, which WPF-UI does not restyle: a
  white rectangle with white headers in a dark window, and an empty white void in the import
  window before a source is chosen. Small-medium: tokens on background, header and rows, in both.
- **E3 — Every InfoBar resizes the remote desktop.** The shell's right column is Auto / Auto /
  Auto / `*`: the InfoBar's row is above the session area, so a message appearing or leaving
  changes the session area's height — measured, the empty state moved 15 px when the delete arming
  expired — and `RdpSession` follows its host's `SizeChanged` with a 300 ms debounce into a
  dynamic-resolution renegotiation. Every status message during a session costs a resize. The
  InfoBar cannot overlay the session (airspace: nothing WPF draws over a `WindowsFormsHost`), so
  the answer is either a reserved row height while a session is open or notices in the tab strip.
  A design decision; medium.
- **E4 — The empty state's glyph did not draw.** `Desktop48` is in the enum and drew nothing;
  found by this pass, on the lot that introduced it. Three probes to the cause: rendering
  off-screen was inconclusive (every symbol gave the same 296 pixels — a fallback box), the font
  folder was first looked for at the wrong path, and then the glyph map of the two TTFs inside
  `Wpf.Ui.dll` answered *present* for U+F083D while `SymbolExtensions.GetString(Desktop48)` returned
  `U+083D U+000F` — the code point above U+FFFF split byte-wise instead of a surrogate pair. A
  WPF-UI 4.3.0 defect: every symbol above the base plane is blank. Fixed here with `Desktop24`
  scaled to the hero size, and the reason written beside it in the markup.

*Fixed the same day, on the `visual` branch, and seen again by driving the 0.4.1-rc.5: E1 (codes in
Core, words in the App, both languages), E2 (WPF-UI's own list and grid), E3 (the notice row keeps
one notice's height while a session is open), E4 (the glyph). Of the four below, E5, E6 and E8 are
fixed there too; E7 is watched, not fixed.*

### What could be better — taste, in order
- **E5 — The *ready* InfoBar never leaves.** *Contrôle RDP v12 prêt — choisissez une connexion…*
  stays until closed, on every launch, and now says what the empty state already says beneath it.
  Informational notices should retire on their own after a few seconds; warnings and errors stay.
- **E6 — Invalid fields are not marked.** The InfoBar lists the errors; the fields themselves stay
  neutral. A red edge on the field named would close the loop.
- **E7 — Fuzzy search is generous.** `iden` matched *Manage credentials* first, correctly, and
  three other rows through scattered letters (`d…e…s`). Harmless with a dozen items; with two
  hundred connections a four-letter query will fill the palette with noise. Watch, do not fix yet.
- **E8 — The search's no-match message names no way out.** The README promises "the empty state
  names the shortcuts"; the pane's says only that nothing matched. A `Ctrl+N` there would help.
