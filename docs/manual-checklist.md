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

## Detached session windows

None of these boxes is ticked yet: the feature shipped its code on 2026-09-01, its human
probe has not been run. It is not closed until this section is. Design:
`docs/superpowers/specs/2026-09-01-detached-windows-design.md`. Two monitors are needed
for most of it; `TEST-VM` is the reference target.

### Detaching and reattaching

- [ ] **Drag a tab out.** Press a tab and pull it **more than 40 px downwards**, out of the
      strip: the drag stops being a reorder and a window appears under the cursor with that
      session in it. Pulling sideways within the strip still **reorders**, as in lot 4 —
      the two gestures must not be confused.
- [ ] The detached session **is still a session**: `Ctrl+K` lists it, the InfoBar's session
      count includes it, and the tab strip simply no longer draws its tab. With the only
      tab detached, the main window shows the empty state, not a black rectangle.
- [ ] `Ctrl+Shift+D` on the active docked session detaches it too — **including while the
      remote desktop has focus** (low-level hook path). Same from the palette
      (*Detach current session*), which is offered only when the active tab is docked.
- [ ] **Detach a second connection** and move its window to the **second monitor**. Both
      remote desktops keep rendering, and the main window keeps working behind them.
- [ ] **Reattach by dragging**: grab the detached window by its caption strip and move it
      over the main window's tab strip. A drop band lights up under the cursor; release,
      and the session goes back to a tab, still connected, no black frame. With every tab
      detached the strip is 0 px tall — check the band still appears and can be aimed at.
- [ ] Reattach the other window with its **Reattach** button, then detach again and
      reattach with `Ctrl+Shift+D` (from inside the detached window) and from the palette
      (*Reattach this session to the main window*). All four paths give the same result,
      and none of them leaves an empty window behind.
- [ ] The session survives every trip: no reconnection line in the log, no CredSSP prompt,
      the desktop is the same one throughout.

### Full screen

- [ ] `F11` on a detached window: the caption strip **and the InfoBar disappear** and the
      remote desktop is edge to edge. Do it on **both** windows: **two remote desktops in
      full screen on two monitors at once**, while the main window still works.
- [ ] Move the pointer to the top of a full-screen window: **nothing is revealed** — no
      caption, no InfoBar. This is deliberate (a reveal band was implemented and removed:
      resizing the host renegotiates the remote resolution in *Dynamic* mode, so the
      picture jumped whenever the pointer brushed the top edge). The window size must not
      change at all for as long as full screen lasts.
- [ ] `Ctrl+Alt+Pause` inside the remote desktop toggles full screen the same way (it goes
      through the control's `ContainerHandledFullScreen`), and so does the caption strip's
      **Full screen** button. `F11` leaves it again.
- [ ] **Full screen ends by itself when the session stops being connected.** With a window
      in full screen, cut the network: the window comes out of full screen on its own, and
      the caption strip and InfoBar are back with the reason, *Reconnect* and *Copy
      diagnostics* on screen. When the session reconnects it **stays windowed** — there is
      deliberately no automatic return to full screen; `F11` puts it back.
- [ ] Full screen can only be **entered on a connected session**: press `F11` on a failed
      or reconnecting detached window and nothing happens (the log says so). It must never
      produce a chrome-less window with no way back.

### Dynamic resolution and DPI

- [ ] On a *Dynamic* connection, **resize a detached window**: after a short pause the
      remote resolution follows, crisp, exactly as it does in a tab. This is the point the
      design turns on — the session must measure **its own** window, not the main one.
- [ ] **Mixed DPI.** Drag a detached window between two monitors at different scaling
      factors (e.g. 100 % and 150 %): the remote desktop stays crisp and the caption strip
      does not misalign. Known limit, not a defect: remembered geometry is converted with
      the **main window's** DPI scale, so on a mixed-DPI desktop a reopened window lands
      approximately, not to the pixel. The guarantee is a window you can reach.

### Closing

- [ ] `Ctrl+W` inside a detached window closes **that session** cleanly, and the window
      with it. The **cross** of the window does the same thing — it is a session close
      (§6.5 protocol), not just a window close.
- [ ] Clicking the cross twice quickly, or pressing `Ctrl+W` while the close is already
      running, does not close two sessions or throw.
- [ ] **Close the application with two detached windows open.** The main window announces
      the sessions are closing and stays up until they are; the detached windows close with
      them and the process exits (`ShutdownMode.OnMainWindowClose` — the detached windows
      have no `Owner` on purpose). On `TEST-VM`, `query session` shows each session as
      **Disc**, not Active, and **no duplicate** session exists. Reconnect afterwards:
      still one session per user, no zombie.
- [ ] Budget check: the ceiling is now **5 s per session, 30 s overall** (it was 15 s in
      lot 4). With several sessions closing slowly, the application must not exit before
      the protocol has had its 30 s, nor hang beyond it.

### Remembered geometry

- [ ] Detach a connection, move and resize its window, close it, then detach the **same
      connection** again: the window **comes back to the same place and size**.
      `%APPDATA%\RemoteDeck\settings.json` carries a `detachedWindows` entry for it.
- [ ] The geometry is written on all three paths: closing the detached window, **reattaching
      it**, and closing the application with the window still open. Check the file after
      each.
- [ ] **Minimise** a detached window and close the application: the minimised window is
      **not** persisted — the previous entry stays as it was, and reopening does not produce
      a window stuck in a corner.
- [ ] Detach a full-screen window and reopen it: the `fullScreen` flag comes back, and it is
      only re-applied to a session that is actually connected.
- [ ] **Unplug the second monitor**, then detach a connection whose remembered geometry was
      on it: the window opens **on a monitor that is really there**, fully visible, never
      off-screen. Same rule as the main window's geometry in lot 3.
- [ ] Corrupt the `detachedWindows` section by hand (set it to `null`, or truncate the
      file), restart and detach: the app starts and detaches on the default placement, with
      no error dialog.

## Application icon

- [ ] **Explorer** shows the deck icon on `RemoteDeck.exe` — in the extra-large view (the
      256 px frame, the only one stored as a PNG) and in the details view (16 px). Never the
      default .NET icon.
- [ ] **Taskbar and Alt-Tab** show it, and the three stacked cards are still told apart at
      that size rather than reading as one blue blob.
- [ ] **Main window**: the icon sits at the left of the title bar, before *RemoteDeck*.
- [ ] **A detached session window** carries the same icon at the left of its 32 px strip,
      before the status dot — and the strip still drags the window: the icon must not
      swallow the gesture.
- [ ] Switch Windows between light and dark: the icon is unchanged. It is a fixed colour and
      does **not** follow the accent, unlike the rest of the interface — expected, not a bug.
- [ ] After changing anything in `tools/icon/New-RemoteDeckIcon.ps1`, the regenerated
      `RemoteDeck.ico` and `RemoteDeck-32.png` are **committed**: CI publishes what is
      versioned and never generates artwork.

## Session navigation — double-click and the full-screen selector

None of this is covered by automated tests: it all lives in `RemoteDeck.App`, which has no
test project. These boxes are the only verification.

### Double-click to detach and to reattach

- [ ] **Double-click a docked tab**: it leaves for a window of its own, placed where that
      connection was last seen or under the pointer. The remote desktop is the same one —
      it must **not** flash, blank or reconnect.
- [ ] The **single** click still only activates. Click tabs back and forth quickly but
      deliberately on *different* tabs: nothing detaches. Only two clicks on the *same* tab
      inside the double-click time do.
- [ ] **A sideways drag still reorders** and a 40 px downward drag still detaches. Neither
      gesture was replaced.
- [ ] **Double-click the caption strip** of a detached window: it goes back into the tab
      strip. Again, no reconnection.
- [ ] **Dragging the caption strip still moves the window** — the double-click must not have
      cost the drag.
- [ ] Press the **cross** on a detached window, then double-click its caption strip while the
      close protocol is still running: nothing happens. The session must not be moved back
      into the shell mid-close.

### The full-screen session selector

- [ ] With **two or more sessions**, put one full screen (`F11`) and bring the pointer to the
      top edge: the bar slides in and shows **a chip per other session**, each with its
      status dot and the connection's name; the host is in its tooltip.
- [ ] With **one session only**, no chips appear and the bar looks exactly as it did.
- [ ] **Click the chip of a docked session**: the main window comes forward with that tab
      active. The full-screen window stays full screen and connected behind it.
- [ ] **Click the chip of a session detached and full screen on another monitor**: that
      window comes forward, still full screen. Nothing is re-parented on either side.
- [ ] After a click, the bar **retracts on its own** — no `Topmost` strip left floating over
      the main window.
- [ ] Open and close a session while a bar is up: the chips follow, without a flicker of the
      full-screen surface.
- [ ] A chip whose session is reconnecting shows the **amber** dot, and turns green again on
      its own — the chips read state live, they are not a snapshot.
- [ ] Press a chip and slide the pointer off it before releasing: nothing happens.
- [ ] Light and dark: the chips' border, hover and text follow the theme like the rest.

## Build prerequisites (any lot)

- [ ] A clean clone builds with `dotnet build RemoteDeck.sln` on a machine that has the
      Windows SDK or the .NET Framework 4.8 Developer Pack (`TlbImp.exe`). Without it
      the interop generation target fails with a clear message, not a silent skip.
- [ ] `dotnet build RemoteDeck.sln` produces **zero warnings**.
- [ ] Build is x64. **ARM64 is out of scope for v1**: the interop is generated with
      `/machine:X64`. Do not publish an ARM64 artefact without redoing this checklist.
