using System.Runtime.InteropServices;
using System.Windows.Interop;
using RemoteDeck.App.Services;

namespace RemoteDeck.App.Rdp;

/// <summary>
/// Catches application shortcuts before the RDP control swallows them (spec §7.3, R6).
/// Four interchangeable mechanisms; the lot-0 probe keeps the one that fires while the
/// remote session has keyboard focus.
/// </summary>
/// <remarks>
/// Probe outcome, 2026-08-29, Remote Desktop control v12: mechanisms 1-3
/// (<see cref="Mechanism.WpfThreadFilter"/>, <see cref="Mechanism.WinFormsMessageFilter"/>,
/// <see cref="Mechanism.KeyboardHook"/>) were each verified armed and each verified NOT to
/// intercept: with a connected session focused, Ctrl+K and Ctrl+Tab reached the remote desktop
/// and no interception was ever logged. All three are scoped to the UI thread's message
/// retrieval, and mstscax appears to service keyboard input on its own input window/thread, so
/// the messages never pass through them. <see cref="Mechanism.LowLevelKeyboardHook"/> is the
/// one that works — WH_KEYBOARD_LL runs system-wide, ahead of any thread's queue — and is
/// therefore the default mechanism; the other three are kept as diagnostic options, not as
/// credible fallbacks.
/// <para>
/// The §7.3 rule "no synchronous I/O in the low-level callback" is honoured: Windows enforces
/// <c>LowLevelHooksTimeout</c> (300 ms by default) and silently uninstalls a hook that overruns
/// it. <see cref="LowLevelHookCallback"/> only computes the swallow/pass decision — a few
/// <c>GetAsyncKeyState</c> calls, no file access — and posts the log write and the
/// <see cref="Triggered"/> notification to the WPF dispatcher before returning. The other three
/// mechanisms run inside the message pump, where a synchronous write is harmless, and are
/// unchanged.
/// </para>
/// </remarks>
// System.Windows.Forms.* is qualified on purpose: this file lives in a project with
// UseWindowsForms on, and a bare `using System.Windows.Forms;` would collide with the
// WPF types (Application, Message) that the rest of the app uses.
internal sealed class ShortcutInterceptor : IDisposable
{
    public enum Mechanism { WpfThreadFilter, WinFormsMessageFilter, KeyboardHook, LowLevelKeyboardHook }

    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int VkTab = 0x09;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkB = 0x42;
    private const int VkK = 0x4B;
    private const int VkW = 0x57;
    private const int WhKeyboard = 2;
    private const int WhKeyboardLl = 13;

    public event Action<string>? Triggered;

    /// <summary>
    /// Last word on whether a recognised shortcut is actually taken from the keystroke. Called with
    /// the shortcut name (<c>"Ctrl+W"</c>, …) once <see cref="Decide"/> has matched it and before
    /// anything is swallowed; <c>false</c> lets the key through untouched, as if the shortcut had
    /// never been recognised. <c>null</c> — the default — swallows every recognised shortcut.
    /// </summary>
    /// <remarks>
    /// The predicate runs inside the hook callback, under the same rules as
    /// <see cref="LowLevelHookCallback"/>: reading in-memory state only — no I/O, no synchronous
    /// dispatcher hop — or Windows drops the hook on <c>LowLevelHooksTimeout</c>. It is also the
    /// only piece of application code the callback can be made to run, so it is treated as
    /// untrusted: anything it throws is logged and read as <c>true</c>, the behaviour from before
    /// there was a predicate.
    /// </remarks>
    public Func<string, bool>? ShouldIntercept { get; set; }

    private readonly Mechanism _mechanism;
    private readonly WinFormsFilter? _winFormsFilter;
    private readonly HookProc? _hookProc;   // kept alive: the native side holds a raw pointer
    private readonly nint _hook;
    private bool _disposed;

    public ShortcutInterceptor(Mechanism mechanism)
    {
        _mechanism = mechanism;
        switch (mechanism)
        {
            case Mechanism.WpfThreadFilter:
                ComponentDispatcher.ThreadFilterMessage += OnThreadFilterMessage;
                break;
            case Mechanism.WinFormsMessageFilter:
                _winFormsFilter = new WinFormsFilter(this);
                System.Windows.Forms.Application.AddMessageFilter(_winFormsFilter);
                break;
            case Mechanism.KeyboardHook:
                _hookProc = HookCallback;
                _hook = SetWindowsHookEx(WhKeyboard, _hookProc, 0, GetCurrentThreadId());
                if (_hook == 0)
                {
                    throw new InvalidOperationException($"SetWindowsHookEx failed: {Marshal.GetLastWin32Error()}");
                }

                break;
            case Mechanism.LowLevelKeyboardHook:
                _hookProc = LowLevelHookCallback;
                // hMod must be a real module handle for WH_KEYBOARD_LL, and dwThreadId 0 makes the
                // hook global -- that is the whole point: it runs ahead of every thread's queue.
                // The callback still executes on this thread, driven by the WPF dispatcher's pump.
                _hook = SetWindowsHookEx(WhKeyboardLl, _hookProc, GetModuleHandle(null), 0);
                if (_hook == 0)
                {
                    throw new InvalidOperationException($"SetWindowsHookEx(WH_KEYBOARD_LL) failed: {Marshal.GetLastWin32Error()}");
                }

                break;
        }

        ProbeLog.Write("R6", $"ShortcutInterceptor armed with {mechanism}");
    }

    // --- Mechanism 1: WPF dispatcher filter ---
    private void OnThreadFilterMessage(ref MSG msg, ref bool handled)
    {
        if (handled)
        {
            return;
        }

        if (msg.message is not (WmKeyDown or WmSysKeyDown))
        {
            return;
        }

        handled = Handle((int)msg.wParam);
    }

    // --- Mechanism 2: Windows Forms filter ---
    private sealed class WinFormsFilter(ShortcutInterceptor owner) : System.Windows.Forms.IMessageFilter
    {
        public bool PreFilterMessage(ref System.Windows.Forms.Message m)
        {
            if (m.Msg is not (WmKeyDown or WmSysKeyDown))
            {
                return false;
            }

            return owner.Handle((int)m.WParam);
        }
    }

    // --- Mechanism 3: thread-local WH_KEYBOARD hook ---
    private delegate nint HookProc(int code, nint wParam, nint lParam);

    private nint HookCallback(int code, nint wParam, nint lParam)
    {
        // lParam bit 31 set = key up; we only act on key down.
        if (code >= 0 && ((long)lParam & 0x80000000L) == 0 && Handle((int)wParam))
        {
            return 1; // swallow
        }

        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    // --- Mechanism 4: system-wide WH_KEYBOARD_LL hook ---
    private nint LowLevelHookCallback(int code, nint wParam, nint lParam)
    {
        try
        {
            // A global hook sees every keystroke on the desktop, including those meant for other
            // applications. Act only while our own process owns the foreground window.
            if (code >= 0 && (int)wParam is WmKeyDown or WmSysKeyDown && IsForegroundOurs())
            {
                // KBDLLHOOKSTRUCT starts with DWORD vkCode.
                if (HandleLowLevel(Marshal.ReadInt32(lParam)))
                {
                    return 1; // swallow
                }
            }
        }
        catch (Exception ex)
        {
            // Same rule as Handle: nothing escapes a reverse-P/Invoke callback. Falling through
            // to CallNextHookEx leaves the keystroke and the hook chain untouched. This is the
            // one path that still writes synchronously — it cannot happen on a normal keystroke.
            LogSwallowed("LowLevelHookCallback", ex);
        }

        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private static bool IsForegroundOurs()
    {
        nint hwnd = GetForegroundWindow();
        if (hwnd == 0)
        {
            return false;
        }

        _ = GetWindowThreadProcessId(hwnd, out uint processId);
        return processId == (uint)Environment.ProcessId;
    }

    /// <summary>
    /// Reports whether a modifier is currently down.
    /// </summary>
    /// <remarks>
    /// <c>GetKeyState</c> returns the state synchronized with the calling thread's message queue,
    /// which is correct for the three thread-scoped mechanisms: they run while dispatching the
    /// very message that updated it. A low-level hook fires <i>before</i> the key reaches any
    /// queue, and the UI thread's queue state is precisely what mstscax is suspected of bypassing,
    /// so that mechanism reads the asynchronous (physical) state instead.
    /// </remarks>
    private bool IsDown(int virtualKey) => _mechanism == Mechanism.LowLevelKeyboardHook
        ? (GetAsyncKeyState(virtualKey) & 0x8000) != 0
        : (GetKeyState(virtualKey) & 0x8000) != 0;

    /// <summary>
    /// Names the shortcut a key-down maps to, or <c>null</c> when the key is not ours. Pure
    /// decision: a couple of keyboard-state reads, no logging, no event, no I/O — this is what
    /// the low-level callback is allowed to do before it must return.
    /// </summary>
    private string? Decide(int virtualKey)
    {
        if (!IsDown(VkControl))
        {
            return null;
        }

        return virtualKey switch
        {
            // Ctrl+B is also a WPF KeyBinding on the shell, but that one only fires while the
            // application owns the keyboard focus. Going through the interceptor as well is what
            // makes the pane foldable without first leaving the remote session.
            VkB => "Ctrl+B",
            VkK => "Ctrl+K",
            // Ctrl+W closes the active tab. Like Ctrl+B it is also a WPF KeyBinding on the
            // shell, for when the focus is on the WPF side; the interceptor is what makes it work
            // from inside a remote session, where WPF sees no keystroke at all.
            VkW => "Ctrl+W",
            VkTab => IsDown(VkShift) ? "Ctrl+Shift+Tab" : "Ctrl+Tab",
            _ => null,
        };
    }

    /// <summary>
    /// Asks <see cref="ShouldIntercept"/> whether a recognised shortcut is really ours to take.
    /// Synchronous and side-effect-free on the nominal path, so all four mechanisms can call it
    /// straight after <see cref="Decide"/>, the low-level hook included.
    /// </summary>
    /// <remarks>
    /// A predicate that throws must cost neither the keystroke nor the process: the failure is
    /// reported on the deferred <c>[shortcuts]</c> path and read as <c>true</c>, which is exactly
    /// how the interceptor behaved before there was a predicate at all.
    /// </remarks>
    private bool ShouldSwallow(string shortcut)
    {
        // Read once: the shell may replace the predicate from the UI thread while we are in here.
        Func<string, bool>? predicate = ShouldIntercept;
        if (predicate is null)
        {
            return true;
        }

        try
        {
            return predicate(shortcut);
        }
        catch (Exception ex)
        {
            LogDeferred($"ShouldIntercept(\"{shortcut}\") failed: {ex.GetType().Name}: {ex.Message} — intercepting anyway");
            return true;
        }
    }

    /// <summary>
    /// Writes a <c>[shortcuts]</c> line without doing the I/O here: the caller may be the low-level
    /// callback, which has a 300 ms budget for the whole keystroke. Falls back to writing inline
    /// when there is no dispatcher to post to — no application, hence no message pump to starve.
    /// </summary>
    private static void LogDeferred(string message)
    {
        try
        {
            // Fully qualified: UseWindowsForms puts System.Windows.Forms.Application in scope too.
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                ProbeLog.Write("shortcuts", message);
                return;
            }

            _ = dispatcher.BeginInvoke(() =>
            {
                try
                {
                    ProbeLog.Write("shortcuts", message);
                }
                catch
                {
                    // On the UI thread an escaping exception is a crash all the same, and the
                    // logger is the very thing that failed: there is nowhere left to report to.
                }
            });
        }
        catch
        {
            // Same reasoning, for a dispatcher that is shutting down under us.
        }
    }

    /// <summary>
    /// Decides whether a key-down belongs to the application and, if so, raises
    /// <see cref="Triggered"/> synchronously. Returns <c>true</c> when the key was consumed.
    /// Used by the three message-pump mechanisms, which run while the pump dispatches the very
    /// message that carried the key; the low-level hook uses <see cref="HandleLowLevel"/> instead.
    /// </summary>
    /// <remarks>
    /// Nothing may escape this method. With <see cref="Mechanism.KeyboardHook"/> it runs inside a
    /// reverse-P/Invoke callback the OS calls directly, where an unhandled exception does not
    /// unwind into managed code — it terminates the process. Both the log write (synchronous file
    /// I/O) and the <see cref="Triggered"/> subscribers can throw, so the whole body is guarded.
    /// The two message-pump mechanisms are protected the same way, for one behaviour everywhere:
    /// on failure the shortcut is dropped and the key passes through to the session untouched.
    /// </remarks>
    private bool Handle(int virtualKey)
    {
        try
        {
            string? shortcut = Decide(virtualKey);
            if (shortcut is null || !ShouldSwallow(shortcut))
            {
                return false;
            }

            Announce(shortcut);
            return true;
        }
        catch (Exception ex)
        {
            LogSwallowed("Handle", ex);
            return false;
        }
    }

    /// <summary>
    /// Low-level-hook counterpart of <see cref="Handle"/>: computes the decision synchronously and
    /// returns, having only <i>posted</i> the side effects. Returns <c>true</c> to swallow the key.
    /// </summary>
    /// <remarks>
    /// Windows uninstalls a WH_KEYBOARD_LL hook that exceeds <c>LowLevelHooksTimeout</c> (300 ms),
    /// so the log write and the <see cref="Triggered"/> subscribers — arbitrary work, file I/O
    /// included — go to the WPF dispatcher instead of running here. With no dispatcher (the
    /// application is shutting down, or none was ever created) there is nowhere to post to, so the
    /// key is passed through rather than swallowed with nothing to show for it.
    /// </remarks>
    private bool HandleLowLevel(int virtualKey)
    {
        try
        {
            string? shortcut = Decide(virtualKey);
            if (shortcut is null || !ShouldSwallow(shortcut))
            {
                return false;
            }

            // Fully qualified: UseWindowsForms puts System.Windows.Forms.Application in scope too.
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                return false;
            }

            _ = dispatcher.BeginInvoke(() =>
            {
                try
                {
                    Announce(shortcut);
                }
                catch (Exception ex)
                {
                    // On the UI thread an escaping exception is a crash all the same.
                    LogSwallowed("HandleLowLevel(dispatched)", ex);
                }
            });

            return true;
        }
        catch (Exception ex)
        {
            LogSwallowed("HandleLowLevel", ex);
            return false;
        }
    }

    /// <summary>Records the interception and notifies subscribers. Never called from the LL callback itself.</summary>
    private void Announce(string shortcut)
    {
        ProbeLog.Write("R6", $"{shortcut} intercepted by {_mechanism}");
        Triggered?.Invoke(shortcut);
    }

    private static void LogSwallowed(string origin, Exception ex)
    {
        try
        {
            ProbeLog.Write("R6", $"{origin} failed: {ex.GetType().Name}: {ex.Message}");
        }
        catch
        {
            // The logger is the very thing that may have failed; there is nowhere left to
            // report to, and reporting is not worth killing the process over.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        switch (_mechanism)
        {
            case Mechanism.WpfThreadFilter:
                ComponentDispatcher.ThreadFilterMessage -= OnThreadFilterMessage;
                break;
            case Mechanism.WinFormsMessageFilter:
                if (_winFormsFilter is not null)
                {
                    System.Windows.Forms.Application.RemoveMessageFilter(_winFormsFilter);
                }

                break;
            case Mechanism.KeyboardHook:
            case Mechanism.LowLevelKeyboardHook:
                if (_hook != 0)
                {
                    UnhookWindowsHookEx(_hook);
                }

                break;
        }
    }

    [DllImport("user32.dll")] private static extern short GetKeyState(int virtualKey);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int virtualKey);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? lpModuleName);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int idHook, HookProc lpfn, nint hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hhk);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
