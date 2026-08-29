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
/// candidate that follows: WH_KEYBOARD_LL runs system-wide, ahead of any thread's queue.
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
    private const int VkK = 0x4B;
    private const int WhKeyboard = 2;
    private const int WhKeyboardLl = 13;

    public event Action<string>? Triggered;

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
                if (Handle(Marshal.ReadInt32(lParam)))
                {
                    return 1; // swallow
                }
            }
        }
        catch (Exception ex)
        {
            // Same rule as Handle: nothing escapes a reverse-P/Invoke callback. Falling through
            // to CallNextHookEx leaves the keystroke and the hook chain untouched.
            try
            {
                ProbeLog.Write("R6", $"LowLevelHookCallback failed: {ex.GetType().Name}: {ex.Message}");
            }
            catch
            {
                // Nowhere left to report to.
            }
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
    /// Decides whether a key-down belongs to the application and, if so, raises
    /// <see cref="Triggered"/>. Returns <c>true</c> when the key was consumed.
    /// </summary>
    /// <remarks>
    /// Nothing may escape this method. With <see cref="Mechanism.KeyboardHook"/> and
    /// <see cref="Mechanism.LowLevelKeyboardHook"/> it runs inside a
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
            bool ctrl = IsDown(VkControl);
            if (!ctrl)
            {
                return false;
            }

            string? shortcut = virtualKey switch
            {
                VkK => "Ctrl+K",
                VkTab => IsDown(VkShift) ? "Ctrl+Shift+Tab" : "Ctrl+Tab",
                _ => null,
            };
            if (shortcut is null)
            {
                return false;
            }

            ProbeLog.Write("R6", $"{shortcut} intercepted by {_mechanism}");
            Triggered?.Invoke(shortcut);
            return true;
        }
        catch (Exception ex)
        {
            try
            {
                ProbeLog.Write("R6", $"Handle failed: {ex.GetType().Name}: {ex.Message}");
            }
            catch
            {
                // The logger is the very thing that may have failed; there is nowhere left to
                // report to, and reporting is not worth killing the process over.
            }

            return false;
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
