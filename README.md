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
