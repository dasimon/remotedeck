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

### V1 — There is no motion at all
Zero `Storyboard`, `DoubleAnimation` or transition in 2,095 lines of XAML. The palette — Acrylic,
round corners — pops into existence; the InfoBar appears; the active tab snaps. A 120–160 ms
opacity-and-4-px-translate on palette open and close, on InfoBar entrance, and on the tab indicator
is the single change that would most alter how the application *feels*, and it costs nothing at
runtime. Medium.

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
