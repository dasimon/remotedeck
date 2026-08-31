# Changelog

All notable changes to RemoteDeck are recorded here. Dates are ISO 8601.

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
