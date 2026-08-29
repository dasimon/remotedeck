using System.Runtime.InteropServices;
using System.Windows.Interop;
using RemoteDeck.App.Services;

namespace RemoteDeck.App.Rdp;

/// <summary>
/// Catches application shortcuts before the RDP control swallows them (spec §7.3, R6).
/// Three interchangeable mechanisms; the lot-0 probe picks the first that fires while
/// the remote session has keyboard focus.
/// </summary>
// System.Windows.Forms.* is qualified on purpose: this file lives in a project with
// UseWindowsForms on, and a bare `using System.Windows.Forms;` would collide with the
// WPF types (Application, Message) that the rest of the app uses.
internal sealed class ShortcutInterceptor : IDisposable
{
    public enum Mechanism { WpfThreadFilter, WinFormsMessageFilter, KeyboardHook }

    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int VkTab = 0x09;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkK = 0x4B;
    private const int WhKeyboard = 2;

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

    /// <summary>
    /// Decides whether a key-down belongs to the application and, if so, raises
    /// <see cref="Triggered"/>. Returns <c>true</c> when the key was consumed.
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
            bool ctrl = (GetKeyState(VkControl) & 0x8000) != 0;
            if (!ctrl)
            {
                return false;
            }

            string? shortcut = virtualKey switch
            {
                VkK => "Ctrl+K",
                VkTab => (GetKeyState(VkShift) & 0x8000) != 0 ? "Ctrl+Shift+Tab" : "Ctrl+Tab",
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
                if (_hook != 0)
                {
                    UnhookWindowsHookEx(_hook);
                }

                break;
        }
    }

    [DllImport("user32.dll")] private static extern short GetKeyState(int virtualKey);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int idHook, HookProc lpfn, nint hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hhk);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
