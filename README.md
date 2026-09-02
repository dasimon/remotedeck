# RemoteDeck

A keyboard-first Remote Desktop (RDP) connection manager for Windows 10/11.
Tabs, sessions you can pull out into their own window, groups, fuzzy search, a `Ctrl+K`
command palette, import from `.rdp` files and from the machines `mstsc` remembers, and a
credential vault backed by Windows DPAPI — built on the native Remote Desktop ActiveX
control, so the RDP protocol itself is Microsoft's, not ours. The interface is available
in English and French.

> Status: **pre-alpha**. Lots 0–5 are done: multi-session tabs, automatic
> reconnection, dynamic resolution, a searchable connection pane, an editor, the
> credential vault, the command palette, connection import and a fully translated
> interface. See [`CHANGELOG.md`](CHANGELOG.md) for what 0.1.0 contains — and for
> detached session windows, which are in the tree but unreleased.

## Requirements

- Windows 10 20H2+ or Windows 11
- .NET 10 SDK to build

## Build

    dotnet build RemoteDeck.sln
    dotnet test  RemoteDeck.sln

RemoteDeck talks to the Remote Desktop ActiveX control through a COM interop
assembly that is **generated at build time** and never committed: the build runs
`TlbImp.exe` over `%SystemRoot%\System32\mstscax.dll` and drops
`Interop.MSTSCLib.dll` into `obj/`. `TlbImp.exe` ships with the Windows SDK, so you
need the **Windows SDK or the .NET Framework 4.8 Developer Pack** installed in
addition to the .NET 10 SDK. (A plain `<COMReference>` would be the idiomatic way
to express this, but its MSBuild task exists only in .NET Framework MSBuild and
fails under `dotnet build` with `error MSB4803`.) The build looks for the tool
under `%ProgramFiles(x86)%\Microsoft SDKs\Windows\v10.0A\bin\NETFX 4.8 Tools`;
if yours lives elsewhere, point at it explicitly:

    dotnet build RemoteDeck.sln -p:TlbImpPath="C:\path\to\TlbImp.exe"

## Usage

### First launch

RemoteDeck creates `%APPDATA%\RemoteDeck\` on first start and opens on an empty
connection pane.

1. Press `Ctrl+N` and fill in at least a name and a host. Everything else —
   port, group, display mode, redirections — has a working default.
2. To connect without being prompted for a password, create a credential first
   (*Manage credentials*), then pick it in the connection editor. A connection
   with no credential still works: the Remote Desktop control raises its own
   CredSSP prompt.
3. Select a connection and press `Enter`.

Already using Remote Desktop Connection? Skip step 1 and **import** what you have —
see below.

### Command palette

`Ctrl+K` opens a palette over everything, including a live remote desktop. Type a
couple of letters and press `Enter`. One list holds three kinds of entry:

- **Commands** — *New connection*, *Import connections…*, *Manage credentials*,
  *Toggle the pane*, *Close session*, *Reconnect*, and — depending on where you opened
  the palette from — *Detach current session* or *Reattach this session to the main
  window*.
- **Open tabs** — jump straight to a session you already have.
- **Every saved connection** — including the ones the search box is currently
  filtering out. Choosing one connects it, or brings its tab forward if it is
  already open.

Matching is fuzzy and ignores case and accents, and the characters your query hit are
highlighted. `↑`/`↓` move, `Enter` runs, `Escape` closes, a single click on a row runs
it, and clicking outside closes the palette.

### Importing existing connections

*Import connections…* (from the palette) reads two sources:

- **A folder of `.rdp` files** — every file with a `full address` becomes a proposed
  connection: host and port, user name, domain, resolution, clipboard, printer, drive
  and audio redirection, server authentication level. Anything RemoteDeck does not map
  is counted and reported as `n unsupported entries ignored`, never guessed.
- **The machines `mstsc` remembers** — `HKCU\Software\Microsoft\Terminal Server
  Client\Servers`. Those carry a host name and sometimes a user name, nothing else.

Nothing is written until you click *Import*. Each proposed row is dated against what
you already have, on host and port: **New** (ticked), **Already imported** (unticked,
and it names the connection it matches) or **Duplicate of …** for a repeat inside the
same batch. That status is advice, not a veto — tick a duplicate deliberately and it
is imported.

**No password is ever imported, and no credential is ever created.** The encrypted
blob in an `.rdp` file belongs to another Windows profile and is discarded without
being read. A user name found in a source is shown to you in the preview and is *not*
written into the connection — create the credential yourself and pick it in the
editor.

### Sessions

Each connection you open gets its **own tab**, and every session stays live in the
background: switching tabs never disconnects anything. A connection has at most one
tab — connecting it again just brings its tab forward. The dot on each tab is its
state: green connected, amber reconnecting, red failed.

Close a tab with `Ctrl+W`, the cross, or a middle-click. Closing the window closes
every session properly first — the app waits for the servers to acknowledge, so
sessions are left *disconnected*, never as zombies.

**Detached windows — one session per monitor.** A session can leave the tab strip and
live in its own window. **Double-click a tab**, or **drag it more than 40 px downwards**
out of the strip, and it becomes a window under your cursor; `Ctrl+Shift+D` and the palette
(*Detach current session*) do the same thing without the mouse. To bring it back,
**double-click the window's caption strip**, drag that strip onto the tab strip — a drop
band lights up — or use its **Reattach** button, `Ctrl+Shift+D`, or the palette. Nothing
reconnects on the way:
moving a session between the main window and its own window is a re-parenting, not a new
connection, so the desktop you were looking at is still there.

A detached session is still one of your sessions — it is in the command palette and in
the session count, it just is not in the visible strip. The window's **cross closes the
session**, the same way `Ctrl+W` does; and closing the main window closes the
application, detached windows included, still waiting for each server to acknowledge
(5 seconds per session, 30 overall).

**Full screen.** `F11`, `Ctrl+Alt+Pause`, or the button in the caption strip puts a
detached window in full screen on the monitor it sits on: the caption strip and the
InfoBar go away and the remote desktop is edge to edge. Two windows on two monitors give
you two full-screen remote desktops at once.

Move the pointer to the top of the screen and a **connection bar** slides in, the way
`mstsc`'s does: the session's name and host, *Reattach*, *Leave full screen*, and a cross.
It also stays up for three seconds when full screen is entered, so two sessions on two
monitors say which is which. The bar is a window of its own, floating over the remote
desktop — it never takes a pixel from the session, which matters in *Dynamic* mode, where
shrinking the surface would renegotiate the remote resolution and make the picture jump
every time you brushed the top edge.

**The other sessions are on that bar too**, one chip each, with their status dot. Click one
and you go straight to it — a detached session is brought forward in its own window,
keeping the full screen it was in; a docked one is activated and the main window raised.
The session you came from stays exactly as it was, full screen and connected: this is
navigation, not a move, so nothing is re-parented and nothing reconnects. With a single
session open, no chips appear.

The window **leaves full screen on its own the moment the session stops being connected**,
so the reason, *Reconnect* and *Copy diagnostics* are on screen when they matter. After a
reconnection it stays windowed; press `F11` when you want it back. Full screen can only be
entered on a connected session.

Where a detached window was — position, size and whether it was full screen — is
remembered per connection in `settings.json`, and reapplied the next time you detach that
connection. A window that was minimised is not recorded. If the monitor it was on is
gone, the window is placed on one that is actually connected rather than off-screen.
On a desktop mixing display scaling factors the placement is approximate: screen
coordinates are converted with the main window's DPI scale, so what is guaranteed is a
window you can reach, not pixel accuracy.

**Automatic reconnection.** When a session drops on a network failure — a timeout or
a lost socket — RemoteDeck reconnects on its own: five attempts, waiting 2 s, 5 s,
10 s, 30 s then 60 s, with a visible countdown you can cancel at any time. It
deliberately does **not** retry anything else: a refused password is never retried
(that is how an Active Directory account gets locked out), and neither is an
unresolvable host name, a certificate failure or a licensing failure. Those stop at
the first attempt with the reason spelled out, a *Reconnect* button and *Copy
diagnostics*.

**Dynamic resolution.** Connections in *Dynamic* display mode resize the remote
desktop to match the window: resize or maximise, and after a short pause the remote
resolution follows, sharp, instead of being stretched. Against a server that refuses
it, the session falls back to scaling the image and says so in the log.

Connections and credentials live in `%APPDATA%\RemoteDeck\connections.db`.
Window and pane layout — pane width, collapsed state, window size and position, and
where each detached session window was — lives beside it in
**`%APPDATA%\RemoteDeck\settings.json`**, deliberately outside the migrated database.
Deleting that file only costs you the layout; the app falls back to its defaults
without complaining.

### Keyboard

| Shortcut | Action |
|---|---|
| `Ctrl+K` | Command palette — connections, open tabs and commands in one list |
| `Ctrl+N` | New connection |
| `Ctrl+F` | Focus the search box (expands the pane if it is collapsed) |
| `Enter` | Connect the selected connection |
| `F2` | Edit the selected connection |
| `Delete` | Delete the selected connection — press twice; the first press only arms it, and the confirmation expires after 5 seconds |
| `Ctrl+B` | Collapse or restore the connection pane |
| `Ctrl+Tab` / `Ctrl+Shift+Tab` | Next / previous session tab (cycles; the session you leave stays connected) |
| `Ctrl+W` | Close the active session tab — or, in a detached window, that session |
| `Ctrl+Shift+D` | Detach the active session into its own window — or reattach it, pressed from the detached window |
| `F11` / `Ctrl+Alt+Pause` | Full screen on and off, in a detached window |

Shortcuts go to the **active window**. In a detached session window, `Ctrl+W` closes that
session, `Ctrl+K` opens the palette over it, `Ctrl+Shift+D` reattaches it and `F11`
toggles its full screen; `Ctrl+Tab`, `Ctrl+Shift+Tab` and `Ctrl+B` have nothing to act on
there, so they are left to the remote desktop instead of being swallowed.

Search is fuzzy and ignores case and accents; it matches on name, host and group
name, sorts favorites first, and highlights the characters your query hit.

**Shortcuts and text fields.** RemoteDeck grabs shortcuts with a low-level keyboard
hook, which is the only mechanism that reaches them while the remote desktop has
focus. So that typing never feels broken, `Ctrl+Tab`, `Ctrl+Shift+Tab`, `Ctrl+W` and
`Ctrl+B` are **left to the text field that has the keyboard focus** — a text box, a
password box or an editable combo box. Everywhere else they do what the table says.
`Ctrl+K` is the exception: it always opens the palette, from inside a text field
included.

If a security policy blocks the low-level keyboard hook, application shortcuts
cannot be intercepted while the remote desktop has focus. `Ctrl+Alt+Left` /
`Ctrl+Alt+Right` still hand the focus back to RemoteDeck — the control raises
that one itself, hook or no hook.

### Language

The interface ships in **English and French** and follows your Windows display
language — there is nothing to configure. To check the other one without changing
Windows, set `REMOTEDECK_UI_CULTURE` before starting (`en-US`, `fr-FR`); an
unrecognised value is ignored and the system language is kept.

Two things stay in English whatever the interface language: the log file, which is
meant for diagnosis and bug reports, and the disconnect reasons and validation
messages, which come from the core library. A French interface therefore shows a
French sentence around an English reason. Translating them is a v2 job.

## Credentials

A credential (user name, optional domain, password) is saved once and reused by
any number of connections. The password is encrypted with Windows DPAPI in
`CurrentUser` scope, with 32 bytes of per-credential entropy regenerated on every
save, and stored as a blob in `%APPDATA%\RemoteDeck\connections.db`. It never
exists as a managed string: it is lent to the Remote Desktop control as a native
`BSTR` for the duration of one call, then zeroed.

## Security

RemoteDeck stores credentials encrypted with Windows DPAPI, bound to your
Windows user session. See [`SECURITY.md`](SECURITY.md) for the threat model —
including what DPAPI does **not** protect against.

## SmartScreen

Release binaries are not code-signed. Windows SmartScreen will warn on first
launch: choose *More info* → *Run anyway*. Signing will be reconsidered once
the project has users.

## License

MIT — see `LICENSE`.
