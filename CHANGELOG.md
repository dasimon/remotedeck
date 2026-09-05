# Changelog

All notable changes to RemoteDeck are recorded here. Dates are ISO 8601.

## Unreleased

### Hardening

Five small changes from a full read of the application, each with the measurement that
motivated it. The full findings, including what was found to be right, are in
`docs/plans/2026-09-05-full-pass-review.md`.

- **A connection that says nothing about server authentication now gets a warning, not
  silence.** Measured 2026-09-05: left to itself, the Remote Desktop control's
  `AuthenticationLevel` is **0** — "no authentication of the server" — so every connection created
  with the editor's "Default" would have accepted a spoofed host without a word. RemoteDeck now
  sets the level on every connection, and its default is 2, "attempt authentication and prompt on
  failure" — what `mstsc.exe` itself writes into every `.rdp` it saves. An explicit 0 chosen in the
  editor stays 0. The editor's option now reads *Default — prompt if failed* so it says what it
  does, and the control's own value is written to the log once per launch (`[R8]`) so the checklist
  can record it on the real control rather than on a probe.
- **The executable is 16 MB smaller.** RemoteDeck speaks English and French, and shipped the
  satellite assemblies of thirteen languages — 18 MB of `*.resources.dll` from its packages, bundled
  into the single file and mapped at every start. `SatelliteResourceLanguages` now names the two.
  Measured: 175.2 MB → 159.0 MB, the same build.
- **A crash now leaves a line in the log.** There was no unhandled-exception handler anywhere:
  an exception escaping any event handler, or any `async void` after its first `await` — which is
  every handler that connects a session — ended the process with nothing written. The three
  sources (UI thread, other threads, faulted tasks nobody awaited) are now logged as `[crash]`,
  type, HResult, message and stack. Nothing is marked handled: an application that swallows what
  it did not expect keeps running in a state nobody designed.
- **Application shortcuts no longer die silently after a stall.** Windows removes a low-level
  keyboard hook that overruns its 300 ms budget and tells nobody — no message, no error, no API to
  ask. One GC pause or one slow disk write, and every `Ctrl+K`, `Ctrl+W` and `Ctrl+Tab` was gone
  for the rest of the session. The hook is now re-armed every time the shell is activated, which
  turns a permanent failure into one that lasts until the next click; when the unhook reports the
  handle was already gone, the log says so (`[R6]`).
### Seen, then fixed

Six screenshots of the running application — shell, palette, editor, in both themes — and the
changes they asked for. The two that are defects rather than taste come first.

- **The connection editor fits the screen it opens on.** It was about 990 px tall at 100 %, could
  neither resize nor scroll, and on a 1366×768 laptop — or a 1080p one at 150 % — the Save button
  was simply off the screen with no way to reach it. Its height is now capped to the monitor's
  work area, and past the cap the form scrolls between the title bar and the buttons, which stay
  where they are.
- **The pane toggle is no longer the loudest thing on the screen.** It was a checked
  `ToggleButton`, which WPF-UI paints in the accent — the only accent fill in view, louder than
  *New* and than the session itself, for a control whose whole job is to fold a side pane. It is a
  plain icon button now; the pane shows its own state.
- **Every icon-only button has a name a screen reader can say.** Twelve of them — reconnect,
  diagnostics, credentials, detach, reattach, full screen, close a tab — carried a tooltip a
  sighted user could read and nothing for anyone else: WPF does not fall back to the tooltip, so
  each was announced as "button". They now carry the same text as their tooltip, and the state
  pill reads its own word. A convention test keeps it so.
- **The ground behind a remote desktop is a token.** The last two literal colours in any view,
  `#FF000000` for the area a session paints into, are now `RdSessionGround` — the one colour the
  sheet names as not being the theme's, because it is not ours. A convention test refuses the next
  literal.
- The shell's toolbar icons are all 24 px now, where two were 20 — the *copy diagnostics* glyph
  read as a pair of brackets at that size.

### Motion

- **The palette and the InfoBar arrive and leave; they no longer pop.** `Ctrl+K` fades in and
  settles up by six pixels over 150 ms, easing out; Escape, Enter or a click elsewhere fade it out
  over 80 ms, easing in — from every exit, so it never vanishes in one frame. The InfoBar slides in
  from just above with the same arrival and fades on hide; a message replacing one already on
  screen swaps its text in place, so a reconnect's status updates never flicker. Nothing over the
  remote desktop moves, deliberately: a `WindowsFormsHost` sits there, and a desktop that
  "slides" would be a lie about latency.
- **Three durations, and no others.** `RdMotionFast` (80 ms, leaving), `RdMotion` (150 ms,
  arriving) and `RdMotionSlow` (220 ms, the ceiling) join the theme sheet as a closed set, like the
  radii and the heights. The three row-hover fades that already existed — connection list, palette
  rows, session tabs — wrote `0:0:0.15` as a literal; they now name the token. A convention test
  reads the sheet and every view: a literal duration anywhere, a token above the ceiling, or a
  fourth token fails the build's tests, and each guard was proved by breaking it.
- **Windows' "Animation effects" setting is honoured.** Off, every duration is zero: the end state
  is applied at once and nothing waits. One class, `Controls.Motion`, owns the two gestures and the
  switch, so no view has to know.
- **The probe log is bounded and cheaper to write.** It grew without limit — 586 KB and 1,245
  lines on the reference client — and each line opened and closed the file, usually on the UI
  thread. One writer now stays open, flushed per line and shared for reading, and the file rolls
  to `probe-l0.log.1` past 1 MB, so the disk holds two at most.

## 0.4.0 — 2026-09-05

### A connection can wait for its VPN

- **A connection can name the Windows VPN profile it needs.** Before opening the session,
  RemoteDeck checks whether that tunnel is up; if it is not, it says so and offers to raise it
  rather than letting the connection fail with a cryptic RDP error about a host it cannot find.
- **The tunnel goes up silently, and one click connects.** Saying yes to the question raises the
  profile through the RAS API — no console window, nothing to dismiss — and because the dial is
  synchronous, the session opens as soon as the tunnel is really up instead of asking you to press
  connect a second time.
- **No VPN credential is stored, and none ever will be.** RemoteDeck keeps the profile's *name*.
  Windows never hands out a saved VPN password: it returns a *handle* to it — sixteen asterisks —
  which the dial exchanges for the real secret inside Windows. Sixteen asterisks is all RemoteDeck
  ever holds. A second secret store beside the DPAPI vault, for tunnels usually behind MFA anyway,
  would buy nothing and cost the one thing this application is careful about.
- A profile with **nothing saved** is not dialled at all: RemoteDeck says so and points at Windows,
  rather than asking for a password it has promised never to want. The first attempt at this used
  `rasdial`, which dials with no credential — and `RASDIALPARAMS` documents what that means: RAS
  offers the current Windows logon account to the VPN server, which on the reference profile dropped
  the call with RAS 628 while the network flyout connected silently.
- It **never dials on its own**. A connection attempt is not consent to change the machine's
  network state, and a tunnel that comes up by itself is a tunnel nobody knows is up.
- The check is on the path the user asked for. Mounting a **workspace** deliberately skips it:
  those sessions open in series, and stopping that series on a question would turn one dialog
  into six.
- A failure to read the VPN state is **not** read as "the tunnel is down" — that would offer to
  raise one that may already be up. It is logged and the session proceeds, so a broken check is
  never worse than no check.
- Schema V3 adds the column nullable: every existing connection means "no VPN required", which is
  true of all of them.

### A web-account connection can name its account

- **A connection that signs in with a web account can carry the Entra account (UPN) it signs in
  with.** The field appears in the editor only while *Use web account* is ticked, is trimmed on
  save, and is handed to the control as an account hint — no domain, no password. This is what
  `mstsc.exe` does: the `.rdp` it exports has no `username` line, but the client remembers the UPN
  per server under `HKCU\Software\Microsoft\Terminal Server Client\Servers\<host>\UsernameHint` and
  hands it over the same way.
- **Importing brings the identity along.** A `.rdp` file with `enablerdsaadauth:i:1` and no
  `username` line picks up the UPN Remote Desktop Connection remembers for that host, and the
  preview no longer warns that the identity is being left behind. A file *without*
  `enablerdsaadauth` keeps its user name out of the connection, exactly as before.
- A credential attached to a web-account connection is **ignored on connect**: only the UPN goes.
- Schema V4 adds the column nullable, so every existing connection reads back as having no hint —
  which is what they had.
- **This is not what makes the sign-in silent, and it is worth saying so plainly.** A web-account
  connection was asking for the full Microsoft sign-in on every connect while `mstsc.exe` on the
  same client reconnected without a word. The cause turned out to be the Windows WAM broker's
  account cache, not RemoteDeck: once the broker holds an account, `mstscax` acquires the token
  silently for *any* host — proven by running the shipped v0.3.0, whose authentication code is
  byte-identical to this one, on a fresh database with no UPN at all. It connected without a
  prompt. The token path is not private to `mstsc.exe` either: `mstscax.dll` carries the WAM calls
  itself and runs them inside RemoteDeck's own process.
- So the UPN earns its place where the broker cache is **cold** — a fresh machine, a cleared cache —
  or where several accounts are cached and one has to be named. With a warm cache it changes
  nothing, and the way to reproduce the original "prompt every time" is to empty the broker cache,
  not to change RemoteDeck.

### xUnit v3

- **The test project moved from xUnit 2.9.3 to xUnit v3**, which NuGet marks the older package
  deprecated in favour of. Not one assertion changed: the 214 tests compiled and passed as they
  were, because this project only ever used `[Fact]`, one `[Theory]` and the assertion library —
  no `ITestOutputHelper`, no class fixtures, and nothing from `xunit.abstractions`, which v3
  removed and which is where most of the migration pain lives.
- Three packages went away with it. `Microsoft.NET.Test.Sdk`, `xunit.runner.visualstudio` and
  `coverlet.collector` are all VSTest, and an xUnit v3 project is a *Microsoft.Testing.Platform*
  application: an executable that runs its own tests. `xunit.v3` is now the only test dependency.
- **The command to run the tests is `dotnet run --project tests/RemoteDeck.Core.Tests`**, in CI
  and locally. Running the executable is the documented way for such a project, and there is one
  test project, so it replaces `dotnet test RemoteDeck.sln` exactly.
- `dotnet test` additionally does not work on this toolchain: with .NET SDK 10.0.400 and
  Microsoft.Testing.Platform 2.3.3 it passes `--server` with no value, the test application
  rejects it, and the run ends with exit code 5 and zero tests — reproduced with `xunit.v3` as
  the sole reference, after deleting `bin` and `obj`, at project and at solution level. That is
  plumbing between the SDK and the platform, not something this repository configures wrongly.
  `global.json` declares the MTP runner anyway, so a later SDK that fixes the integration makes
  `dotnet test` work again with no change here.

## 0.3.0 — 2026-09-03

Workspaces: name the sessions you have open, arranged the way you arranged them, and get the
whole thing back with one command. Plus a right-click menu on the connection list, two gestures
that were only reachable by dragging, and a way out of a full-screen session.


### Where the settings are, and are not

- **Turning session restore on is now visible at every launch**: the InfoBar says the setting
  is on, and whether it reopened anything. It was the one setting in RemoteDeck with no surface
  of its own — the palette command that toggles it closes on Enter, so the only way to learn its
  state was to reopen the palette and read the subtitle.
- The README gained a **Settings** section saying plainly that there is no settings window and
  why: almost everything RemoteDeck remembers is set by using it — pane width by dragging,
  collapsed by `Ctrl+B`, window geometry by moving it — and comes back because it was observed,
  not configured. Session restore is the exception, which is why it announces itself.
- It also names the three timings that are **fixed and not configurable** — the reconnection
  schedule, the close budget, and how long opening a workspace waits for one session before
  starting the next. A settings window earns its place the day one of them is wrong for a real
  network, not before.
- **Both environment variables are documented**, including `REMOTEDECK_PROBE_SHORTCUTS`, which
  was undocumented. It is a lot 0 leftover, and its three non-default values are the mechanisms
  the probe proved intercept nothing — a way to silently lose every application shortcut. Saying
  so is cheaper than finding out.

### Two things you could not see

- **A workspace can be updated from its own row** — right-click → *Update from the open
  sessions*. Re-capturing is the only way a workspace changes, since there is deliberately no
  editor; until now you had to know that, and retype the name exactly. The entry opens the
  naming window with the name and the auto-connect box already filled, so it lands straight on
  the replace confirmation.
- **The session-restore toggle says what it did.** Pressing Enter on *Reopen the last session at
  startup* flipped the setting and showed nothing at all: the palette closes on Enter, and the
  only way to learn the new state was to reopen the palette and read the subtitle again. A
  setting you cannot confirm you changed is a setting you change twice.

### Workspaces you can see

- **Saved workspaces now sit at the top of the connection pane**, above the connections, each
  with its name and how many connections it holds. Clicking one opens it; right-clicking gives
  *Open* and *Delete*.
- Until now the only way to reach a workspace was `Ctrl+K` and typing its name — which requires
  knowing the feature exists before you can find it. Saving a layout and then having to ask where
  it went is the symptom of a feature that hides.
- The section **disappears entirely** when no workspace is saved, so anyone who never captures a
  layout never sees a heading over nothing.
- Workspaces are their own list rather than rows mixed into the connections: a workspace is not a
  connection, and the row a connection needs — status pill, accent rail, search highlighting —
  has nothing to say about one.
- Deleting from the pane asks the same confirmation as deleting from the palette. Both now go
  through one method, so the confirmation cannot exist on one path and be forgotten on the other.

### The checklist gets shorter

- **Fifteen tests now assert things about the repository itself**, in
  `tests/RemoteDeck.Core.Tests/Conventions/`. They replace boxes a human used to tick, and
  they close a class of bug nothing could catch before: `Strings.Designer.cs` is versioned
  here rather than generated, so a key added to the two `.resx` files and forgotten in the
  designer failed **silently at runtime**, as a label that simply never appeared.
- They check that every key exists in all three files with the property name and the
  `GetString` argument matching it, that neither language holds an orphan, that both use the
  same `{0}`/`{1}` placeholders, that no value is empty, that no user-visible text was left
  as a literal in any XAML, that no `{x:Static}` points at a key that no longer exists, and
  that the icon is committed with all nine of its sizes.
- Each was proved by breaking the thing it guards and watching it fail — a guard test that
  has never failed is a guard test nobody has tested.
- `docs/manual-checklist.md` says at the top what no longer needs a human. Where a box read
  "no missing string" or "no hard-coded English", that half is now proven and what remains is
  whether the French reads well and fits its control.
- The two menu gesture hints became resources like every other visible string, even the ones
  the two languages spell identically: a rule with an exception for whatever happens to match
  today is a rule someone has to remember.

### A right-click menu on the connection list

- **Right-click a connection** and you get *Connect*, *Edit…*, a *Favorite* toggle and *Delete* —
  the place Windows has put a thing's properties for thirty years, and until now the only way to
  reach the editor with a mouse was to know that `F2` existed.
- **Double-click still connects**, and deliberately so. It is the primary action, the one `Enter`
  runs and the one every other connection manager binds it to; moving it to *Edit* would have
  cost the fastest path to the point of the application.
- Right-clicking a row **selects it first**, so the menu acts on the row you aimed at rather than
  on whatever was selected before — the classic way this feature deletes the wrong thing.
- *Delete* in the menu arms the same two-step confirmation as the `Delete` key. A menu entry is
  not a shortcut past a confirmation.
- Right-click the empty space below the last row and the menu shrinks to *New connection* and
  *Import connections…* — the only two actions that need nothing selected.
- **Favorite is now a one-click toggle.** It used to mean opening the editor, ticking a box and
  saving; the pane sorts favorites to the top, so the setting was three clicks further away than
  the thing it controls. It writes that one column and nothing else, so it cannot save a stale
  copy of the rest of the connection over what is on disk.

### Workspaces

- **A workspace names the sessions you already have open — arranged, detached, full
  screen — so the whole thing comes back with one command.** `Ctrl+K` → *Save layout
  as…* captures what is on screen right now; `Ctrl+K` → *Open workspace "PROD"*
  brings it back. There is no editor: saving over an existing name replaces it, after
  confirmation, and that is the only way a workspace changes.
- **Opening a workspace never closes anything.** A connection already running stays
  connected and is only moved to where the workspace remembers it — nothing
  reconnects. Open two workspaces in a row and the screen can end up with more
  sessions than either one describes on its own: a deliberate trade, made so that
  opening a workspace can never end a live production session behind your back.
- Its connections open **one after another, not all at once** — six RDP negotiations
  fired together on a network that has only just come up produce six failures that
  are nobody's fault. A failure stays with its own session, with its own reason,
  *Reconnect* and *Copy diagnostics*; the rest keep opening, and a refused password is
  still never retried.
- **Restoring the last session is off by default.** A separate setting, toggled from
  the palette, snapshots what was open at a clean shutdown and reopens it at the next
  launch; killing the process instead leaves the previous snapshot alone. Launching
  RemoteDeck must never connect to anything nobody asked for, so the switch starts
  off.
- Deleting a connection removes it from every workspace that lists it; deleting a
  workspace removes only the workspace, never the connections. Workspaces live in
  `connections.db`; the last-session snapshot lives in `settings.json`.

### Full screen is no longer a dead end

- **The full-screen connection bar now lists the other sessions**, one chip each with its
  status dot. Click one and you go there: a detached session is brought forward in its own
  window, keeping whatever full screen it was in; a docked one is activated and the main
  window raised. Until now, reaching another session from a full-screen one meant leaving
  full screen or hunting for the right window in Alt-Tab.
- Nothing moves and nothing reconnects — the session you came from stays full screen and
  connected exactly where it was. The chips are navigation, not a re-parenting.
- With a single session open the list is empty and the bar keeps the shape it had.
- Documentation fix: the README still claimed that nothing appears at the top of a
  full-screen session. The connection bar has been there since 0.2.0; the passage
  describing its absence — and the reasoning for it — was never updated when it landed.

### Detaching, in one gesture

- **Double-click a tab to detach it**, and **double-click a detached window's caption strip
  to bring it back**. Both were already possible by dragging — 40 px down out of the strip,
  or the window back onto it — but a drag is a gesture you have to know about, and the
  double-click is the one every other tabbed application has taught. Nothing else changes:
  the window opens where that connection was last seen, or under the pointer, and the
  session is re-parented rather than reconnected, exactly as the drag does it.
- The caption strip's double-click did nothing at all before, so no existing gesture was
  taken away.

### An icon of its own

- **RemoteDeck has an application icon**: three screens stacked into a deck — the pile of
  connections the pane holds. Windows shows it in Explorer, on the taskbar and in Alt-Tab,
  and both title bars carry it too, the main window's and a detached session's. Until now
  the executable wore the default .NET icon and the title bars wore nothing at all, because
  `ExtendsContentIntoTitleBar` replaces the system caption and takes the icon Windows would
  have drawn with it.
- It ships as nine sizes, 16 to 256 px, each one **drawn at its own size** rather than
  shrunk from a single large image: reduced to 16 px, a 256 px render turns the steps
  between the cards into a smear. The geometry lives in
  [`tools/icon/New-RemoteDeckIcon.ps1`](tools/icon/New-RemoteDeckIcon.ps1), which is the
  icon's source — run it and commit what it writes; the release workflow publishes what is
  versioned and does not generate artwork.
- Its colour is fixed, unlike everything else in the interface. An `.ico` is static, so the
  icon cannot follow the Windows accent that the design tokens track.

## 0.2.1 — 2026-09-01

**The published binaries of 0.1.0 and 0.2.0 do not start. Use this one instead.**
Nothing in the application changed; only the way it is packaged.

`PublishSingleFile` bundles managed assemblies and leaves native libraries beside the
executable, and the release attached the executable alone. Downloaded on its own it
therefore raised a `DllNotFoundException` inside a window procedure — which Windows
reports as `0xC000041D`, with no message, no window, and nothing in RemoteDeck's own log,
because the crash happens before the first line is written. Six libraries were missing:
`PresentationNative_cor3`, `wpfgfx_cor3`, `vcruntime140_cor3`, `D3DCompiler_47_cor3`,
`PenImc_cor3` and `e_sqlite3`.

The build now bundles them, so the file really is the single file it claims to be, and
the release workflow refuses to publish if anything is left beside the executable — the
check that would have caught both earlier releases.

## 0.2.0 — 2026-09-01

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
