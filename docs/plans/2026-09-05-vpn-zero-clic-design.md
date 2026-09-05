# Raising a VPN profile without a window — design

2026-09-05. Replaces the `rasdial` child process in `WindowsVpn.Dial` with the RAS API.

## The problem, and what was actually wrong

`WindowsVpn.Dial` runs `rasdial "<profile>"` with no credential, because RemoteDeck stores no VPN
secret. On the reference client that fails with RAS error 628 on `VPN FDC`, while the Windows
network flyout raises the same profile silently. `rasphone -d` works but opens a window.

The cause is documented, not mysterious. `RASDIALPARAMS` says that when `szUserName` **and**
`szPassword` are both empty, "RAS uses the user name and password of the current logon context".
`rasdial` with no argument therefore offers the Windows account to the VPN server, which rejects it
and drops the call — 628, not 691. The flyout does not: it dials with the credentials saved in the
profile.

## The mechanism

`RasGetCredentials` never returns a password. It returns a *handle* to the saved one — sixteen
asterisks — and the documentation says to substitute that handle for the password in a subsequent
call to `RasDial`, which then "retrieves and uses the saved password". So RemoteDeck can dial with
the user's stored credential while only ever holding sixteen asterisks. Nothing is stored, nothing
is asked, and the promise made in `VpnRequirement` — *RemoteDeck holds no VPN credential and never
will* — is kept exactly as written.

`RDEOPT_UseRasCredentials`, considered first, does not exist. Every `RDEOPT_*` flag in the SDK's
`ras.h` (10.0.26100.0) was listed; there is no such flag, and `RASDIALEXTENSIONS` has nothing to do
with stored credentials. That route is a dead end.

## What was measured before any code was written

Read-only probes against the real `VPN FDC` profile, 2026-09-05. No secret was displayed, nothing
was written, nothing was dialled.

| Question | Answer |
| --- | --- |
| The profile | L2TP, per-user, `RememberCredential=True`, `CacheCredentials=1` |
| `NULL` as the phone book | **621**, cannot open the phone book. The explicit `.pbk` path is required |
| `RasGetCredentials`, mask `0x7` | Succeeds: an 11-character user name and the 16-asterisk handle |
| `RasGetCredentials`, mask `0xF` | Everything empty. `RASCM_DefaultCreds` on a per-user entry poisons the answer |
| The returned `dwMask` | Echoes the request, plus an undocumented `0x80000000` bit |
| `RASDIALPARAMSW` `dwSize` | **2120 and 2112 accepted; 2100, 2116, 2124, 2128 rejected with 632** |
| A deliberately unknown entry | 623 — the control that proves the calls reach RAS |

Two of these change the design rather than confirm it. The phone book cannot be left to RAS's
default, and the returned mask cannot be used to decide whether a password exists — only the string
can, because the mask says `Password` even when the field comes back empty.

## The split

`Core` gets the sequence, `App` gets the marshalling, for the same reason `VpnRequirement` is in
`Core` while `ConnectedProfiles` is not: what can be wrong here is the order of the steps and the
decision at each one, and that is what a test can hold.

**`Core/Sessions/VpnDialer.cs`** — a pure sequence behind `IRasGateway`
(`ReadEntry`, `ReadCredentials`, `Dial`, `ConnectedProfiles`, `Describe`), returning a
`VpnDialResult`: `Connected`, `RaisedButNotVisible`, `NoStoredCredential`, `EntryNotFound` or
`Failed`.

1. Validate the entry name — trimmed, non-blank, no longer than `RAS_MaxEntryName` (256). A name RAS
   cannot hold names no entry, and is reported as unknown without calling RAS at all.
2. Try the phone books in order: the user's, the all-users one, and only then RAS's default. 621 and
   623 move on to the next; any other error stops there and is reported in Windows's own words.
3. If the entry's dial parameters carry no usable credential, ask `RasGetCredentials`. If there is
   still no password handle, stop: **`NoStoredCredential`, and no dial is attempted**. Dialling
   without a credential is precisely what produces today's 628.
4. Dial. On success, re-read the connected profiles: a profile that does not appear is
   `RaisedButNotVisible` rather than a success nobody can see.

A credential is *usable* only when it has both a password handle and a user name. A password with no
user name would dial as the logon context — today's failure, wearing a different hat.

**`App/Services/RasApi.cs`** — the gateway, verified by hand.

- **No versioned structure is declared.** A raw byte buffer with constant offsets (1034, 1548, 2062),
  which is the same rule `WindowsVpn` already follows for `RASCONN`: do not assume the shape of
  someone else's type. Here the offsets are not assumed either — they were confirmed by reading the
  real profile's user name and handle back out of the buffer.
- `dwSize` is **negotiated**: 2120 first, 2112 on 632, and the value that worked is logged.
- The buffer is zeroed before use and again after, though it never holds more than sixteen
  asterisks.
- `RasGetErrorStringW` supplies the message text, in the user's Windows language.
- **`RasHangUp` is never called.** The documentation asks for it on any non-null handle; obeying it
  would hang up the tunnel just raised. `rasdial.exe` sets the same precedent: it returns and leaves
  the connection standing.

## What the shell does with it

The confirmation dialog stays. RemoteDeck still never dials on its own — a connection attempt is not
consent to change the machine's network state. What changes is what follows a yes: `RasDial` with no
notifier is synchronous, so when it returns the tunnel is either up or it is not, and on `Connected`
the session opens immediately instead of asking the user to connect a second time. One click.

The dial runs on a background thread with a 60-second cap on the wait; past that the shell says the
tunnel is still coming up. The RAS attempt itself continues in the service — the cap is ours, not
Windows's.

## Deliberate limits

No EAP. `RasDial`'s documentation asks for `RasGetEapUserIdentity` first, and the reference profile
is PAP/CHAP/MSCHAPv2, not EAP. An EAP profile will fail with a clear RAS code rather than silently;
coding a branch nobody here can test would be worse than saying so in the checklist.

The one thing no read-only probe can settle is whether `RasDial` accepts the handle against this
particular server. If 628 comes back even with an explicit credential, the diagnosis above is wrong
and the next place to look is the IPsec layer, not the credential.
