# RemoteDeck

A keyboard-first Remote Desktop (RDP) connection manager for Windows 10/11.
Tabs, groups, fuzzy search, a command palette, and a credential vault backed by
Windows DPAPI — built on the native Remote Desktop ActiveX control, so the RDP
protocol itself is Microsoft's, not ours.

> Status: **pre-alpha**. Lot 0 (skeleton and risk probes) in progress.

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

## Security

RemoteDeck stores credentials encrypted with Windows DPAPI, bound to your
Windows user session. See `SECURITY.md` (to be published with v1) for the threat
model — including what DPAPI does **not** protect against.

## SmartScreen

Release binaries are not code-signed. Windows SmartScreen will warn on first
launch: choose *More info* → *Run anyway*. Signing will be reconsidered once
the project has users.

## License

MIT — see `LICENSE`.
