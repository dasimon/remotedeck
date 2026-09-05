# Security Policy

## Supported versions

RemoteDeck is pre-1.0. There are no maintained release branches: only the current
`main` receives fixes. Report against the latest commit on `main`.

## How credentials are stored

Credentials live in the SQLite database at
`%APPDATA%\RemoteDeck\connections.db`. Each credential row holds a `UserName`, an
encrypted secret blob, and 32 bytes of entropy.

- The secret is encrypted with **Windows DPAPI**, `DataProtectionScope.CurrentUser`
  (`ProtectedData.Protect`). The key material is derived from your Windows user
  profile and is held by Windows, not by RemoteDeck.
- A **32-byte entropy value** is drawn from `RandomNumberGenerator` and passed to
  DPAPI as the optional entropy. It is regenerated on **every save**, and stored
  next to the blob. It does not replace DPAPI, it adds to it: two credentials
  sharing the same password produce different blobs.
- **There is no encryption key in the binary.** RemoteDeck ships no secret of its
  own, so a stolen copy of the executable grants nothing.
- **The secret never exists as a managed string.** On save it is read from the
  WPF `PasswordBox` as a `SecureString`, converted to a native `BSTR`, and handed
  to the vault; on connect it goes DPAPI blob → UTF-8 bytes → native `BSTR`, which
  is *lent* to the Remote Desktop control for the duration of one call and then
  zeroed and freed in a `finally`. Every intermediate `byte[]`/`char[]` is cleared
  with `CryptographicOperations.ZeroMemory` or `Array.Clear`.
  `ICredentialVault` exposes no member that accepts or returns a `string` for a
  secret — the API is `Seal(Credential, nint)` and `UseSecret(Credential, Action<nint>)`.
  Source: `src/RemoteDeck.Core/Security/`.
- Log files (`%APPDATA%\RemoteDeck\logs\`) record credentials by label and user
  name only, never by secret.

## Secrets RemoteDeck deliberately does not hold

Two things a user might reasonably expect to find in the vault are not there, and the
reasoning is the same in both cases: Windows already holds them, and a second copy would
be a second thing to lose.

- **A web-account (Entra) token.** A connection with *Use web account* authenticates
  through the Windows account broker; the token never passes through RemoteDeck. What
  the connection may carry is a UPN — an account *name*, handed to the control as a hint.
  A credential attached to such a connection is ignored on connect: no domain and no
  password reach the control.
- **A VPN profile's password.** RemoteDeck stores the profile's *name*, nothing else.
  Raising a tunnel calls `RasDial` with the credential the user saved in the Windows
  profile — and Windows does not hand that password out. `RasGetCredentials` returns a
  *handle* to it (sixteen asterisks, per its documentation), which `RasDial` exchanges
  for the real secret internally. The handle is all that ever exists in RemoteDeck's
  memory; the buffer holding it is zeroed before and after use, and the probe log records
  outcomes and RAS codes only. A profile with **no** saved credential is not dialled at
  all, because dialling would mean asking for a password RemoteDeck has promised never to
  want.

## Threat model — what this covers

- **The database file taken on its own.** `connections.db` copied to another
  machine, or read by another user, is unusable: DPAPI CurrentUser blobs cannot be
  decrypted outside your Windows profile.
- **Backups.** Same reasoning — a file-level backup of `%APPDATA%` carries no
  usable secret.
- **Other Windows accounts on the same machine.** A different (non-administrator)
  user cannot decrypt your blobs, even with read access to the file.

## What this does NOT cover

These limits are stated rather than left implicit.

- **Malware running inside your unlocked Windows session.** Code running as you
  can call `ProtectedData.Unprotect` exactly as RemoteDeck does. No local
  credential store protects against this — not RemoteDeck's, not any other.
- **Local administrators**, who can act on your profile and your processes.
- **Access to the process memory.** While a session is being opened, the secret is
  in memory in cleartext. The zeroing above shortens that window, it does not
  remove it.
- **The Remote Desktop control necessarily receives the plaintext password**, as
  any RDP client must, in order to authenticate to the remote host. Once handed
  over, the secret is under Microsoft's `mstscax` control, not ours.
- **A VPN profile whose credential Windows has saved can be dialled by anything
  running as you** — that is what saving it means, and it is true of `rasdial`, of the
  network flyout, and of RemoteDeck alike. RemoteDeck adds no capability here: it asks
  Windows to use a credential Windows already agreed to reuse, and only when the user
  answers a dialog. It never dials on its own.

## Other notes

**Low-level keyboard hook.** RemoteDeck installs a process-wide low-level keyboard
hook (`SetWindowsHookEx` with `WH_KEYBOARD_LL`). This is required, not optional:
when the Remote Desktop ActiveX control has focus it consumes keystrokes before
they reach the application's message loop, so thread-scoped mechanisms never see
them, and application shortcuts (`Ctrl+K`, `Ctrl+Tab`) would be impossible.

What the hook does: for each key-down event, while our own process owns the
foreground window, it reads the virtual-key code and decides whether that one key
is an application shortcut to swallow. What it does not do: it does not record,
store, buffer, or transmit keystrokes; keys that are not application shortcuts are
passed on untouched via `CallNextHookEx`. Source:
`src/RemoteDeck.App/Rdp/ShortcutInterceptor.cs`.

A security policy (EDR, GPO) may forbid installing such a hook. RemoteDeck then
runs without application shortcuts while the remote session has focus; the control's
own `Ctrl+Alt+Left` / `Ctrl+Alt+Right` still returns focus to the application.

## Reporting a vulnerability

Use **GitHub private vulnerability reporting** on the repository:
<https://github.com/dasimon/remotedeck/security/advisories/new>.

Please do **not** open a public issue for a security bug. Include the affected
commit, what you observed, and how to reproduce it. This is a spare-time project:
expect an acknowledgement within a few days rather than within hours.
