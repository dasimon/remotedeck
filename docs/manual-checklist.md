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

## Lot 4 — sessions

None of these boxes is ticked yet: lot 4 shipped its code on 2026-08-30, its human
probe has not been run. The lot is not closed until this section is.

### Tabs

- [ ] Connect three connections in a row: **three tabs**, each with its own remote
      desktop. The lot 3 item "only one session at a time" no longer applies — it is
      superseded here.
- [ ] Connecting a connection that **already has a tab** brings that tab forward
      instead of opening a second one.
- [ ] `Ctrl+Tab` and `Ctrl+Shift+Tab` cycle through the tabs — including **while the
      remote desktop has focus** (low-level hook path) — and the session being left
      **does not disconnect**: come back to it and the desktop is still there, no
      reconnection, no black frame. This is the D12 rule (`Hidden`, never `Collapsed`).
- [ ] The status dot follows the state: **green** connected, **amber** interrupted or
      reconnecting, **red** failed, neutral otherwise.
- [ ] Drag a tab onto another position: the order changes and the sessions survive it.

### Dynamic resolution (D6)

- [ ] On a connection in **Dynamic** display mode, resize the window (or drag the
      splitter): after a short pause the **remote resolution changes** — the remote
      desktop is redrawn crisp at the new size, it is not stretched. Dragging the window
      edge continuously produces **one** resolution change at the end, not a hundred.
- [ ] Shrinking the pane almost shut never asks for a desktop below **640×480**.
- [ ] Against a server that refuses `UpdateSessionDisplaySettings` (older host), the
      session falls back to **SmartSizing** — the image is scaled, blurry but complete —
      the log carries `fallback to SmartSizing` once, and no further resize is attempted
      for that session.
- [ ] In **Fixed** and **Scaled** modes the remote resolution does **not** follow the
      window (expected: only Dynamic does).

### Reconnection

- [ ] With a session connected, **cut the network** (disable the adapter, or unplug):
      the tab goes **amber**, the InfoBar counts down (`retry in 2 s`, then 5, 10, 30,
      60) and names the attempt number out of 5.
- [ ] **Restore the network during the countdown**: the session comes back on its own,
      the tab is green again and the attempt counter is back to 0.
- [ ] Leave the network down for the whole budget (~107 s over five attempts): the tab
      ends **red / `Failed`**, the countdown is gone, and *Reconnect* is offered.
- [ ] Press **Cancel** during a countdown: the retry is abandoned immediately, the tab
      is red, and *Reconnect* replaces *Cancel* (the two are never on screen together).
- [ ] Press **Reconnect** on a failed tab: it reconnects with a **fresh budget** and,
      for a connection with a credential, **without prompting for a password** — the
      vault lends the secret again for every attempt. The log shows
      `Password supplied from credential …` once per attempt.
- [ ] **Expire or disable the account**, then let the session drop: the tab goes red
      **immediately**, with **no retry at all**, and the InfoBar names the cause
      (logon failed / account locked out / password expired). Reconnecting in a loop on
      a refused credential is the one behaviour that must never happen — check the log
      shows no `attempt n/5` line for that code.
- [ ] Point a connection at a **nonexistent host name**: no automatic retry either
      (name resolution is not a transient network failure), red tab, "DNS name lookup
      failed" or "Host not found".
- [ ] **Copy diagnostics** puts a readable block in the clipboard: connection, host,
      display mode, control version, state, attempts, disconnect code, category,
      meaning, extended reason, Windows wording, SmartSizing status, UTC timestamp.

### Closing

- [ ] `Ctrl+W` closes the active tab; the neighbouring tab becomes active and its
      session is untouched.
- [ ] **Middle-click** on a tab closes it, the way a browser does.
- [ ] Closing the last tab brings back the empty state; the app does not close.
- [ ] With **two sessions live**, close the window: the InfoBar announces the sessions
      are closing, the window stays up until they are, then closes. On each server,
      `query session` shows the session as **Disc** (not Active) and **no duplicate**
      session exists. Reconnect afterwards: still one session, no zombie.
- [ ] Pressing `Ctrl+W` twice quickly, or clicking the cross while a close is running,
      does not close two tabs or throw.

### Known limitations to confirm (not regressions)

- [x] ~~`Ctrl+W` typed inside a **text box** still closes the active tab~~ — **fixed in
      lot 5**. The hook now asks the shell before swallowing a keystroke (§7.3, reserve 1).
      This is checked in the "Lot 5 — palette, import, language" section below, not here.

## Lot 5 — palette, import, language

None of these boxes is ticked yet: lot 5 shipped its code on 2026-08-31, its human probe
has not been run. The lot is not closed until this section is, and neither are the five
success criteria of §1.

### Command palette (`Ctrl+K`)

- [ ] `Ctrl+K` opens the palette **while the remote desktop has focus** (low-level hook
      path) as well as from the connection pane. The palette appears centred on the main
      window and does **not** float above other applications.
- [ ] With no query, the list shows, in that order: the six commands (*New connection*,
      *Import connections…*, *Manage credentials*, *Toggle the pane*, *Close session*,
      *Reconnect*), then one row per **open session tab**, then the saved connections
      alphabetically. Every saved connection is listed, **even those the pane's search box
      is currently filtering out**.
- [ ] Type **two letters** of a connection name: it is on top, and the matched characters
      are **highlighted** in the title and in the subtitle. Accents and case are ignored —
      `ser` finds *Serveur*, `SER` and `sér` find it too.
- [ ] Type a word that matches nothing: the list is empty, an explanatory line replaces
      it, and `Enter` does **not** close the palette (you are mid-query).
- [ ] `↓` and `↑` move the selection, and moving past the last drawn row **scrolls** it
      into view.
- [ ] `Enter` runs the selected row: a connection opens its tab (or brings its existing
      tab forward), a command runs, a tab row switches to that tab.
- [ ] `Escape` closes the palette and does nothing else. Clicking **outside** the palette
      closes it too — and it does **not** close itself the instant it opens.
- [ ] A **single click** on a row runs it, without needing a second click. A click on the
      scrollbar, in the list's padding, or anywhere that is not a row does **nothing**.
- [ ] Typing `disconnect` still finds the *Close session* row (the word lives in its
      subtitle; there is deliberately no second *Disconnect* entry).
- [ ] With no database available (degraded mode), the palette still opens and offers the
      commands alone, without throwing.

### Import (`Ctrl+K` → *Import connections…*)

- [ ] **Folder of `.rdp` files**: pick a folder, and every `.rdp` carrying a
      `full address` shows up — name from the file name, address, source, status. A file
      with no `full address` produces no row.
- [ ] A file using `full address:s:host:3390` shows **port 3390**; one using
      `server port:i:3390` shows **3389** and counts that entry as ignored (`server port`
      is deliberately not read — §8).
- [ ] A file with unknown or malformed entries carries **one** warning of the form
      `4 unsupported entries ignored` in its tooltip — not four separate ones.
- [ ] A file containing `password 51:b:…` imports cleanly and **no warning mentions a
      password**. Check the log too: the blob must appear nowhere.
- [ ] A file carrying `username:s:` shows the user name as a **warning in the preview**;
      after import, open the connection editor: the user name is **not** in the notes and
      **no credential** was created (the credential field is empty). Open *Manage
      credentials*: the list is unchanged.
- [ ] **Registry source**: *From the mstsc registry* lists the hosts Remote Desktop
      Connection remembers, port 3389, no credential. On a machine that never used
      `mstsc`, it says the preview is empty — it does not report an error.
- [ ] **Duplicates**: import a folder twice. The second time every row is *Already
      imported*, names the saved connection it matches, and is **unticked**. Two files
      pointing at the same host and port in one batch: the second is *Duplicate of « X »*
      and unticked.
- [ ] **Tick a duplicate by hand and import**: it *is* imported (the status is advice,
      not a veto) and the second copy appears in the pane.
- [ ] *Select all new* ticks only the new rows; *Clear* unticks everything.
- [ ] Nothing is written before *Import* is clicked: browse both sources, close the
      window, and the connection pane is unchanged.
- [ ] After a successful import the connections appear in the pane and connect normally
      (the Remote Desktop control raises its own CredSSP prompt, since there is no
      credential).

### Keyboard hook and text inputs (§7.3, reserve 1)

- [ ] In the pane's **search box**, `Ctrl+W` no longer closes the active tab, `Ctrl+B` no
      longer collapses the pane, and `Ctrl+Tab` moves the focus instead of switching tabs.
      Same in the connection editor's text fields and in a password field.
- [ ] With the focus **anywhere else** (the connection list, the tab strip, the remote
      desktop), `Ctrl+W`, `Ctrl+B` and `Ctrl+Tab` behave exactly as in lot 4.
- [ ] `Ctrl+K` opens the palette **even from inside a text box** — it is never handed
      back to the input.
- [ ] With the connection editor or the credentials window in front, `Ctrl+W` does **not**
      close the session hidden behind it (the shell ignores shortcuts while it is not the
      active window).

### Language

- [ ] On a French Windows, the application starts **in French** with no configuration.
- [ ] `set REMOTEDECK_UI_CULTURE=en-US` then start: the interface is in English.
      `REMOTEDECK_UI_CULTURE=zz` starts normally in the system language and says so in
      the log — it must never prevent startup.
- [ ] French pass over the **four modal windows** — *Connection editor*, *Credentials*,
      *Credential editor*, *Import*: every label, button, title, placeholder, tooltip and
      status message is in French, with no leftover English and no missing string.
- [ ] **Watch for truncation**: the label columns of the connection editor and the
      credential editor are fixed at **120 px** and **110 px**. French labels are longer
      than English ones — check that none is cut off or ellipsised at those widths, at
      the default window size and after a resize.
- [ ] Also check the import window's column headers (200/200/180/180 px) and its summary
      line, and the palette's placeholder and empty-state lines.
- [ ] **Known and expected**: disconnect reasons and validation messages come from
      `RemoteDeck.Core` and stay **in English** inside the French interface — the shell
      wraps them in a French sentence but does not translate them (§9). Confirm this is
      the known behaviour, not a missing translation.
- [ ] Plurals read correctly in French for 0, 1 and several items (import tallies,
      reconnection attempts).

## Build prerequisites (any lot)

- [ ] A clean clone builds with `dotnet build RemoteDeck.sln` on a machine that has the
      Windows SDK or the .NET Framework 4.8 Developer Pack (`TlbImp.exe`). Without it
      the interop generation target fails with a clear message, not a silent skip.
- [ ] `dotnet build RemoteDeck.sln` produces **zero warnings**.
- [ ] Build is x64. **ARM64 is out of scope for v1**: the interop is generated with
      `/machine:X64`. Do not publish an ARM64 artefact without redoing this checklist.
