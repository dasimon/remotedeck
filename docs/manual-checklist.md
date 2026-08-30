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
      window. Known lot 0 ergonomics, **fixed in lot 3** — see the lot 3 section below,
      which supersedes this item.

## Lot 3 — connection pane

### Editor

- [ ] `Ctrl+N` opens the connection editor. Saving with an empty name, an empty host, a
      host containing a space, or a port outside 1–65535 is **refused**, and every reason
      is listed at once in the editor's InfoBar — no `MessageBox`, no partial save.
- [ ] With the display mode set to *Dynamic*, the fixed width/height fields are disabled
      and their content never blocks saving. Switching to *Fixed* re-enables them and a
      size below 640×480 (or above 8192) is refused.
- [ ] A saved connection reappears in the pane immediately, in the right group, with the
      star set if *Favorite* was ticked.

### Connecting from the list

- [ ] Select a connection **with** a credential attached and press `Enter`: the session
      opens without any prompt, and the log shows `Password supplied from credential …`.
- [ ] Select a connection **with no credential** (or one whose credential was deleted):
      RemoteDeck puts **no** password and the control raises its own **CredSSP prompt**.
      Typing the password there must connect. RemoteDeck itself has no manual password
      entry any more.
- [ ] Connect a second connection while one is live: the InfoBar announces the current
      session is closing, and the new one opens once it is closed. Only one session at a
      time in lot 3.

### Search

- [ ] Type an unaccented query for an accented name (`repl` for `Répliqué`, `SEDE` for
      `Sédé…`): the row still matches, in either case. Same for an accented query on an
      unaccented name.
- [ ] The matching characters are **highlighted in the accent colour**, and the highlight
      lands on the right letters of the *original* text, accents included.
- [ ] The query matches on name, host **and** group name.
- [ ] **Favorites come first**, even when a non-favorite scores higher on the query.
- [ ] Typing fast filters once at the pause, not once per keystroke (120 ms debounce);
      the list never flickers and the selection behaves.
- [ ] A query matching nothing shows the empty state, which names the shortcuts.

### Keyboard

- [ ] `Ctrl+F` focuses the search box and selects its content, **and un-collapses the
      pane first** if it was collapsed.
- [ ] `F2` opens the editor on the selected connection — from the list and from the
      search box.
- [ ] `Delete` on a selected row **arms** the deletion (warning InfoBar, "Press Delete
      again to confirm"); a second `Delete` on the same row deletes it.
- [ ] Arming a row and then pressing `Delete` on a **different** row only re-arms the new
      one — nothing is deleted.
- [ ] Wait **5 s** after arming: the InfoBar closes on its own and the next `Delete` arms
      again instead of deleting.
- [ ] `Delete` inside the **search box** deletes characters; it never arms a deletion.
- [ ] `Ctrl+B` collapses and restores the pane **while the RDP control has focus**
      (low-level hook path, §7.3) and with the focus in the WPF chrome (`InputBinding`
      path). It must never fire twice for one press.

### Layout persistence

- [ ] Drag the splitter to a new width, collapse/expand the pane, move and resize the
      window, then close and restart: the pane width, the collapsed state and the window
      bounds come back. `%APPDATA%\RemoteDeck\settings.json` holds them.
- [ ] Save the geometry on a second monitor, unplug it, restart: the window opens
      **centred on the remaining desktop**, not off-screen.
- [ ] Corrupt `settings.json` by hand (truncate it mid-object), restart: the app starts
      on the default layout, with no error dialog.

### Degraded mode

- [ ] Make `%APPDATA%\RemoteDeck\connections.db` unreadable (lock it, or point the app at
      a directory it cannot write): the app still starts, the pane is replaced by its
      "unavailable" message, the InfoBar says the database is unavailable, and `Ctrl+N`,
      `F2`, `Delete` and *Manage credentials* answer with a warning instead of throwing.

### Theme

- [ ] Switch Windows between **light and dark** with the credential editor open: the
      restyled `PasswordBox` follows — border, background, focus underline and the
      placeholder text all change with the theme and stay legible in both.
- [ ] The placeholder disappears as soon as one character is typed and comes back when
      the field is cleared; it aligns with the text of the `ui:TextBox` above it.

## Build prerequisites (any lot)

- [ ] A clean clone builds with `dotnet build RemoteDeck.sln` on a machine that has the
      Windows SDK or the .NET Framework 4.8 Developer Pack (`TlbImp.exe`). Without it
      the interop generation target fails with a clear message, not a silent skip.
- [ ] `dotnet build RemoteDeck.sln` produces **zero warnings**.
- [ ] Build is x64. **ARM64 is out of scope for v1**: the interop is generated with
      `/machine:X64`. Do not publish an ARM64 artefact without redoing this checklist.
