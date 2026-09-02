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

**Workspaces.** A workspace is a named set of the sessions you already have open —
arranged, detached, full screen — so the whole thing comes back with one command
instead of by hand every morning. Open the palette (`Ctrl+K`) → *Save layout as…*,
give it a name, and leave *Connect automatically* ticked unless you only want the
windows placed, not connected. There is no separate editor for a workspace: saving
over an existing name replaces it, after a confirmation, and that replacement is how
a workspace changes.

Open one from the palette, *Open workspace "PROD"*. **Opening a workspace never
closes anything.** A connection that is not running yet is opened; one that already
is stays connected and is only moved to where the workspace remembers it — nothing
reconnects. Open two workspaces in a row and the screen can end up holding more
sessions than either one describes on its own: that is the trade RemoteDeck makes so
that opening a workspace can never end a live session behind your back.

Its connections open **one after another, not all at once** — six RDP negotiations
fired together on a network that has only just come up produce six failures that are
nobody's fault. A failure stays with its own session: it shows its own reason, with
*Reconnect* and *Copy diagnostics*, exactly as a connection opened by hand would, and
the rest keep opening. A refused password inside a workspace is still never retried.

*One after another* means the next connection is not even issued until the previous
session has answered — connected, dropped or failed. A machine that answers nothing at
all holds the workspace for five seconds, the same budget a session gets to answer the
close protocol, and then the next one starts anyway. So a six-machine workspace takes
longer to be complete than firing six connections at once would: that wait is what is
being bought.

One limitation worth knowing, and it only concerns a session that is **already** full
screen: a workspace that wants it full screen leaves it where it is rather than moving
it to the monitor it recorded. Doing it properly would mean leaving full screen, moving
the window, and re-entering full screen: two full-screen transitions and two
remote-resolution renegotiations just to relocate a picture that is already on screen.
Mounting the same workspace on a machine that is not running yet is unaffected — the
session is opened, detached to the recorded rectangle, and put full screen there as soon
as it is connected. So opening "INCIDENT" puts its full-screen session on the recorded
monitor whenever it opens that session itself; when that session is already full screen
on another monitor, it stays there, still connected and untouched.

Deleting a connection removes it from every workspace that lists it; deleting a
workspace removes only the workspace, never the connections it names — both ask for
confirmation first. A workspace whose connections have all been deleted this way
stays listed, since it is still the name you chose, but opening it shows "Nothing to
open" instead of doing anything.

Workspaces live in `connections.db`, alongside your connections. Separately, a
setting — **off by default**, toggled from the palette — snapshots the sessions you
had open at a clean shutdown and reopens them the next time RemoteDeck starts;
launching the app must never connect to anything you did not ask for, which is why it
starts off. Killing the process instead of closing normally leaves the previous
snapshot alone rather than replacing it with nothing. That snapshot lives in
`settings.json`, not in a workspace.

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

Connections, credentials and workspaces live in `%APPDATA%\RemoteDeck\connections.db`.
Window and pane layout — pane width, collapsed state, window size and position, where
each detached session window was, and the last-session-restore switch and its snapshot
— lives beside it in **`%APPDATA%\RemoteDeck\settings.json`**, deliberately outside the
migrated database. Deleting that file only costs you the layout, plus the restore
switch reverting to off and its snapshot disappearing with it: nothing in it is content
you composed or a secret, and the app falls back to its defaults without complaining.

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

**Mouse.** A **double-click** on a connection connects it — the primary action, the same one
`Enter` runs, and the same convention every other connection manager follows. Configuring a
connection is the *secondary* action, so it lives where Windows has always put it: **right-click
→ Edit…**. The row menu also carries *Connect*, a *Favorite* toggle, and *Delete* — which arms
the same two-step confirmation the `Delete` key does, rather than deleting outright. Right-click
in the empty space below the last row instead and you get *New connection* and *Import
connections…*, the two actions that need nothing selected.

Right-clicking a row **selects it first**. The menu always acts on what you aimed at, never on
whatever was selected before.

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
