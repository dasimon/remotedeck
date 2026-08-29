# Manual verification checklist

Run before tagging a release. Items are grouped by the lot that introduced them.
Automated tests cover `RemoteDeck.Core`; everything below touches COM or WPF and
cannot be automated reliably.

Probe evidence for the lot 0 items lives in
`docs/superpowers/probes/l0-probe-results.md`.

## Lot 0 — control hosting

### Control selection

- [ ] App starts on a machine with `mstscax.dll`; the InfoBar names the control version.
- [ ] App starts on a machine **without** the newest control (or with the CLSID list
      temporarily reordered): an older version is picked, no crash.
- [ ] **Registered-but-not-creatable fallback.** On the reference machine (Windows 11
      10.0.26200) the version-13 CLSID `3f859aa3-c2d4-4faa-b0e4-fd0c9c4e5e3a` has a
      complete `InprocServer32` key yet `CoGetClassObject` returns `0x80040111`
      (`CLASS_E_CLASSNOTAVAILABLE`). Confirm the log contains
      `[R4] CLSID … is registered but not creatable` **and** that the app then hosts
      version 12 (`1df7c823-b2d4-4b54-975a-f2ac5d7cf8b8`) instead of throwing.
      Registry presence alone must never be treated as usable.

### Session

- [ ] Connect with valid credentials: remote desktop renders inside the window.
- [ ] Connect to a nonexistent host: InfoBar shows an error with reason code and
      Windows description; no MessageBox.
- [ ] Wrong password: no crash; `OnLogonError` or CredSSP prompt.
- [ ] Close the window while connected: on the server, `query session` shows the
      session as Disc (not Active), and no duplicate session exists.
- [ ] Disconnect reason **1** (`disconnectReasonLocalNotError`) is **not** shown as an
      error. It is the code the control raises for our own `RequestClose`, and
      `GetErrorDescription(1, 0)` returns a misleading "internal error" text that must
      not reach the user. Same rule as reason 3 (`disconnectReasonByServer`).

### Display

- [ ] **Mixed DPI — still unverified.** The lot 0 probe machine was single-DPI
      (`[R3] Window DPI scale X=1,00 Y=1,00`), so this was never observed. Move the
      window between monitors with different scaling factors (e.g. 100 % and 150 %)
      and confirm the remote desktop stays crisp and the chrome does not misalign.
      Until someone ticks this box, PerMonitorV2 is assumed, not proven.
- [ ] The remote desktop does **not** resize with the window in lot 0 — this is
      expected. Dynamic resolution ships in lot 4 (design D6); re-verify there.

### Keyboard

- [ ] `Ctrl+K` and `Ctrl+Tab` are intercepted while the remote desktop has focus, and
      the keystrokes do **not** reach the remote session. The working mechanism is the
      low-level hook (`WH_KEYBOARD_LL`, `ShortcutInterceptor.Mechanism.LowLevelKeyboardHook`);
      the three thread-scoped mechanisms (`WpfThreadFilter`, `WinFormsMessageFilter`,
      thread `WH_KEYBOARD`) arm without error but intercept nothing — do not accept
      "the hook is armed" as evidence, check the `[R6] … intercepted` log line.
- [ ] While the app is foreground, the shortcuts are swallowed **everywhere**, text
      boxes included. Until lot 5 filters on the focused control, confirm this is still
      the known behaviour and not a new regression.
- [ ] In an environment where a low-level hook cannot be installed (EDR, group policy),
      `Ctrl+Alt+Left` / `Ctrl+Alt+Right` still release focus from the control through
      the native `OnFocusReleased` event — the user is never trapped in the session.

### Window chrome

- [ ] Title bar drag, double-click maximise and system buttons work with the RDP
      control visible.
- [ ] Mica renders on the title bar and the left pane on Windows 11; the RDP surface
      is opaque (expected).
- [ ] The password field is a **native WPF `PasswordBox`** (required for
      `SecurePassword`); it has no placeholder text and inputs do not stretch with the
      window. Known lot 0 ergonomics, scheduled for restyling in lot 3 — confirm
      nothing worse appeared.

## Build prerequisites (any lot)

- [ ] A clean clone builds with `dotnet build RemoteDeck.sln` on a machine that has the
      Windows SDK or the .NET Framework 4.8 Developer Pack (`TlbImp.exe`). Without it
      the interop generation target fails with a clear message, not a silent skip.
- [ ] `dotnet build RemoteDeck.sln` produces **zero warnings**.
- [ ] Build is x64. **ARM64 is out of scope for v1**: the interop is generated with
      `/machine:X64`. Do not publish an ARM64 artefact without redoing this checklist.
