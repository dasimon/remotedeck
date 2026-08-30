# RemoteDeck

A keyboard-first Remote Desktop (RDP) connection manager for Windows 10/11.
Tabs, groups, fuzzy search, a command palette, and a credential vault backed by
Windows DPAPI — built on the native Remote Desktop ActiveX control, so the RDP
protocol itself is Microsoft's, not ours.

> Status: **pre-alpha**. Lots 0–3 are done: one session at a time, a searchable
> connection pane, an editor and the credential vault. Multi-session tabs,
> automatic reconnection and the command palette land in lots 4 and 5.

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

Connections and credentials live in `%APPDATA%\RemoteDeck\connections.db`.
Window and pane layout — pane width, collapsed state, window size and position —
lives beside it in **`%APPDATA%\RemoteDeck\settings.json`**, deliberately outside
the migrated database. Deleting that file only costs you the layout; the app
falls back to its defaults without complaining.

### Keyboard

| Shortcut | Action |
|---|---|
| `Ctrl+N` | New connection |
| `Ctrl+F` | Focus the search box (expands the pane if it is collapsed) |
| `Enter` | Connect the selected connection |
| `F2` | Edit the selected connection |
| `Delete` | Delete the selected connection — press twice; the first press only arms it, and the confirmation expires after 5 seconds |
| `Ctrl+B` | Collapse or restore the connection pane |

Search is fuzzy and ignores case and accents; it matches on name, host and group
name, sorts favorites first, and highlights the characters your query hit.

`Ctrl+K` (command palette), `Ctrl+Tab` / `Ctrl+Shift+Tab` (switch tab) and
`Ctrl+W` (close tab) are part of the design but ship with later lots.

If a security policy blocks the low-level keyboard hook, application shortcuts
cannot be intercepted while the remote desktop has focus. `Ctrl+Alt+Left` /
`Ctrl+Alt+Right` still hand the focus back to RemoteDeck — the control raises
that one itself, hook or no hook.

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
