# Changelog

All notable changes to RemoteDeck are recorded here. Dates are ISO 8601.

## 0.2.0 — unreleased

Two things. A session no longer has to live inside the main window — pull it out, put it
on a second monitor, make it full screen, and put it back when you are done. And the
interface has been rebuilt on a single sheet of design tokens, so it finally looks like
one application rather than a set of screens that grew separately.

### Detached session windows

- **Detach a session into its own window.** Drag a tab **more than 40 px downwards**, out
  of the tab strip, and it becomes a window under your cursor. `Ctrl+Shift+D` and the
  palette entry *Detach current session* do the same thing without the mouse. Dragging
  sideways inside the strip still reorders tabs, as before.
- **Put it back** by dragging the window's caption strip onto the tab strip — a drop band
  lights up where it will land — or with the window's *Reattach* button, `Ctrl+Shift+D`,
  or the palette.
- **Nothing reconnects on the way.** Moving a session between the main window and its own
  window re-parents the Remote Desktop control; it does not open a new connection. No
  password is presented again, no black frame, no reconnection line in the log.
- **A detached session is still one of your sessions.** It stays in the command palette
  and in the session count — it only leaves the visible tab strip. The window's **cross
  closes the session**, exactly as `Ctrl+W` does, and waits for the server to acknowledge
  first.
- **Full screen, one desktop per monitor.** `F11`, `Ctrl+Alt+Pause` or the button in the
  caption strip hides the strip and the InfoBar and puts the remote desktop edge to edge
  on the monitor the window sits on. Two windows on two monitors give two full-screen
  remote desktops at once, with the main window still working behind them.
- While full screen lasts, the window **never changes size**, and nothing is revealed when
  you move the pointer to the top of the screen. That is on purpose: showing a bar would
  resize the remote surface, and in *Dynamic* mode that renegotiates the remote
  resolution — the picture would jump every time you brushed the top edge. Instead the
  window **leaves full screen by itself the moment the session stops being connected**, so
  the reason, *Reconnect* and *Copy diagnostics* are on screen when they matter. After a
  reconnection it stays windowed; `F11` puts it back when you ask. Full screen can only be
  entered on a connected session.
- **Dynamic resolution follows the window it is actually in**, so resizing a detached
  window resizes the remote desktop just as resizing the main one does.
- **Closing the main window closes the application**, detached windows included. Every
  session is still disconnected properly first — 5 seconds per session, 30 seconds
  overall, raised from the previous 15-second ceiling now that the number of sessions is
  no longer bounded by what fits in a tab strip.
- **Where each window was is remembered**, per connection, in `settings.json`: position,
  size and whether it was full screen, written when the window closes, when the session is
  reattached, and when the application exits. A minimised window is not recorded. If the
  monitor it was on is gone, the window opens on one that is actually connected instead of
  off-screen.

### Visual refresh

- **One sheet of design tokens.** Surfaces, borders, text, accent, state colours, three
  radii and four heights now live in a single file every view reads from. A value outside
  those sets is a defect you can catch by reading, instead of a judgement call.
- **The accent stays yours.** Every token derives from the Windows theme, so RemoteDeck
  follows your system accent colour and your light/dark setting — including a switch made
  while it is running, and including windows opened after that switch.
- **Selecting a connection no longer floods the row.** A selected row keeps its own text
  colour on a lightly tinted ground with a 3 px accent rail at its left edge, instead of a
  solid block of accent with white text on it.
- **Keyboard focus is visible at last.** RemoteDeck is driven from the keyboard and never
  showed where you were; the focused row now carries an accent outline, distinct from the
  selected row, because the two are not always the same row.
- **State is readable without colour.** *Connected*, *Reconnecting* and *Offline* appear
  as a labelled pill beside the status dot, so the state survives a reader who cannot
  separate red from green.
- **Tabs belong to the area they command.** The active tab shares the session area's
  background, interrupts the strip's hairline underneath itself and carries a thin accent
  fillet on top; inactive tabs simply recede, with no vertical dividers between them.
- **The command palette reads like one.** Each row pairs an icon with a title and a
  subtitle that says what the command *does* rather than restating its name, and its
  shortcut is drawn as a key on the right. The current row uses the same accent rail as
  the connection list, so one gesture means "you are here" everywhere.

### Known limitations

- **Mixed-DPI placement is approximate.** Screen coordinates are converted using the main
  window's display scaling, so a remembered window lands exactly where it was on a desktop
  where every monitor shares one scaling factor, and near enough on one where they do not.
  What is guaranteed is a window you can reach, not pixel accuracy.
- **Still one monitor per session.** A detached window fills one display; spanning a
  single session across several monitors is a different feature and is still out of scope.

### Verification

`RemoteDeck.Core` is covered by **171 automated tests**, up from 155 — the close budget,
the screen fitting and the saved geometry are all testable and tested. Everything that
touches windows, drag gestures, the Remote Desktop control or the way a state looks has
to be checked by hand: the *Detached session windows* section of
[`docs/manual-checklist.md`](docs/manual-checklist.md) has been run against a live host,
including two full-screen sessions on two monitors and a `query session` on the server
after closing the application with detached windows open — no session was left behind.

## 0.1.0 — 2026-08-31

First usable version. RemoteDeck is a connection manager for Windows Remote Desktop:
it keeps your machines organised, opens them in tabs and remembers your passwords
safely. It does not reimplement RDP — it hosts the Remote Desktop control that ships
with Windows, so the protocol, the rendering and the security are Microsoft's.

Everything below is new; there is no previous release to compare against.

### Connections

- A left-hand pane lists every saved connection, grouped, with favourites pinned to
  the top and a star to add or remove one.
- **Fuzzy search** that ignores case and accents and matches on name, host and group
  name. It highlights the letters your query hit, so you can see why a line matched.
  Filtering happens in memory, so it keeps up with typing.
- An editor for each connection: name, host, port, group, credential, display mode,
  fixed resolution, clipboard / printer / drive / audio redirection, server
  authentication level, notes, and an experimental Entra ID (web account) option.
  Invalid input is refused with a sentence, not a beep.
- Deleting is a two-step action: the first press only arms it, and the confirmation
  expires by itself after five seconds. There is no modal *Are you sure?* anywhere in
  the application.
- Connections and credentials live in a small SQLite database under
  `%APPDATA%\RemoteDeck\connections.db`, created on first launch and upgraded in
  place. A database written by a *newer* RemoteDeck is refused rather than damaged.
  Window and pane layout live separately in `settings.json`; deleting it costs you
  nothing but the layout.

### Sessions

- **Tabs.** Every connection you open gets its own tab, and every session stays alive
  in the background — switching tabs never disconnects anything. A connection has at
  most one tab; opening it again brings that tab forward. Tabs can be reordered by
  dragging and closed with the cross, a middle-click or `Ctrl+W`.
- A coloured dot on each tab shows its state: green connected, amber reconnecting,
  red failed.
- **Automatic reconnection** after a network failure: five attempts, waiting 2, 5, 10,
  30 then 60 seconds, with a countdown you can cancel. It deliberately retries
  *nothing else* — a refused password is never retried, because that is how an Active
  Directory account gets locked out, and neither is an unknown host name, a
  certificate failure or a licensing failure. The password is lent again from the
  vault for each attempt, so you are never prompted mid-reconnection.
- **Explicit errors.** Forty-seven documented disconnect codes are turned into a
  readable cause and a severity, with *Reconnect*, *Cancel* and *Copy diagnostics*
  offered where they make sense. An unknown code still shows its raw value rather
  than swallowing it.
- **Dynamic resolution.** In *Dynamic* display mode the remote desktop is re-sized to
  match the window instead of being stretched: resize or maximise, and after a short
  pause the remote resolution follows, sharp. Against a server that refuses it, the
  session falls back to scaling the image and says so once.
- **Clean shutdown.** Closing a tab, or the whole window, waits for each server to
  acknowledge the disconnection before quitting. Sessions are left *disconnected*,
  never as zombies you have to clear by hand.

### Passwords

- A credential — user name, optional domain, password — is saved once and reused by
  any number of connections.
- The password is encrypted with **Windows DPAPI** in `CurrentUser` scope, with 32
  bytes of fresh per-credential entropy on every save. It is bound to your Windows
  session: copying the database file to another machine or another account gives an
  attacker nothing usable.
- The password never exists as a managed string inside RemoteDeck. It is decrypted
  into unmanaged memory, handed to the Remote Desktop control for the length of one
  call, then overwritten. It is never written to the log.
- A connection without a credential still works: the Remote Desktop control raises
  its own password prompt.
- What DPAPI does and does not protect against is written out in
  [`SECURITY.md`](SECURITY.md).

### Command palette

- `Ctrl+K` opens a palette over everything, including a live remote desktop. One list
  holds your saved connections, your open tabs and the application's commands (new
  connection, import, manage credentials, toggle the pane, close session, reconnect).
- Type two letters, press `Enter`. Matching is fuzzy and accent-insensitive, arrows
  move, `Escape` closes, a single click on a row runs it.

### Importing what you already have

- **From a folder of `.rdp` files**: host and port, user name, domain, resolution,
  clipboard / printer / drive / audio redirection and the server authentication level
  are read. Entries RemoteDeck does not understand are counted and reported, never
  guessed at.
- **From the machines `mstsc` remembers** (the Remote Desktop Connection history in
  your own registry hive).
- Nothing is written until you confirm. Each proposed line is marked *New*, *Already
  imported* (naming the connection it matches) or *Duplicate*, on host and port. The
  mark is advice, not a veto: tick a duplicate on purpose and it is imported.
- **No password is ever imported and no credential is ever created.** The encrypted
  blob inside an `.rdp` file belongs to another Windows profile and is discarded
  without being read. A user name found in a file is shown to you and left out of the
  connection.

### Keyboard and language

- Shortcuts: `Ctrl+K` palette, `Ctrl+N` new, `Ctrl+F` search, `Enter` connect, `F2`
  edit, `Delete` delete, `Ctrl+B` collapse the pane, `Ctrl+Tab` / `Ctrl+Shift+Tab`
  next and previous tab, `Ctrl+W` close the tab.
- Shortcuts work **inside a remote session**, where the Remote Desktop control would
  normally eat every keystroke, and they get out of the way inside text fields:
  `Ctrl+Tab`, `Ctrl+Shift+Tab`, `Ctrl+W` and `Ctrl+B` are left to the field you are
  typing in. `Ctrl+K` always opens the palette.
- If a security policy blocks the keyboard hook, `Ctrl+Alt+Left` / `Ctrl+Alt+Right`
  still hand the focus back to RemoteDeck — the Remote Desktop control provides that
  one itself.
- The interface is available in **English and French** and follows your Windows
  display language with nothing to configure.

### Known limitations

These are deliberate for 0.1.0, not defects:

- **One monitor.** A session fills a single display. Spanning several monitors is not
  supported.
- **No RD Gateway.** Connections are direct; there is no support for a Remote Desktop
  Gateway, so a machine you can only reach through one cannot be used.
- **x64 only.** The build is 64-bit Intel/AMD, including when you compile it
  yourself. On an ARM64 machine it runs under emulation; no native ARM64 build is
  produced or tested.
- **The binary is not signed.** Windows **SmartScreen** will warn the first time you
  run a downloaded release: choose *More info* → *Run anyway*. A code-signing
  certificate will be reconsidered once the project has users.
- **Some messages stay in English** even in the French interface: disconnect reasons
  and validation messages come from the core library, which is not translated yet.
  The log file is English by design — it is meant for bug reports.
- Also out of scope for this version: session recording, syncing settings between
  machines, SSH and VNC, scripts before or after connecting, importing from
  mRemoteNG or Royal TS, nested group trees and multiple tags per connection.

### Verification

`RemoteDeck.Core` — the database, the vault, the search, the reconnection policy, the
disconnect codes and the importers — is covered by **155 automated tests**. Anything
that touches the Remote Desktop control or the window system cannot be tested
automatically and is covered by [`docs/manual-checklist.md`](docs/manual-checklist.md),
run before tagging a release. That checklist is **not yet complete for lots 4 and 5**,
which is why 0.1.0 is not tagged.
