using System.Runtime.InteropServices;

namespace VpnRdpTray;

internal sealed class RdpWindowShortcuts : IDisposable
{
    internal const int ShortcutMessage = 0x8041;
    private const int LowLevelKeyboard = 13;
    private const int KeyDown = 0x0100, KeyUp = 0x0101, SystemKeyDown = 0x0104, SystemKeyUp = 0x0105;
    private readonly HookProcedure callback;
    private nint hook, target;
    private HeldKey down, consumed;

    [Flags]
    private enum HeldKey
    {
        None = 0, LeftShift = 1, RightShift = 2, LeftControl = 4, RightControl = 8,
        LeftAlt = 16, RightAlt = 32, LeftWindows = 64, RightWindows = 128,
        FullScreen = 256, Minimize = 512,
        Shift = LeftShift | RightShift, Control = LeftControl | RightControl,
        OtherModifier = LeftAlt | RightAlt | LeftWindows | RightWindows,
        Shortcuts = FullScreen | Minimize
    }

    internal RdpWindowShortcuts() => callback = Filter;

    // Refresh after ActiveX takes focus because Windows calls the newest hook first.
    internal bool Arm(nint window)
    {
        if (window == 0 || GetForegroundWindow() != window)
            return false;
        if (window != target)
        {
            Disarm();
            target = window;
        }

        nint next = SetWindowsHookEx(LowLevelKeyboard, callback, GetModuleHandle(null), 0);
        if (next == 0)
            return hook != 0;
        if (hook != 0)
            UnhookWindowsHookEx(hook);
        else
            ReadHeldKeys();
        hook = next;
        return true;
    }

    internal void Disarm()
    {
        target = 0;
        if (hook != 0)
            UnhookWindowsHookEx(hook);
        hook = 0;
        down = consumed = HeldKey.None;
    }

    public void Dispose() => Disarm();

    private nint Filter(int code, nint message, nint data)
    {
        int kind = unchecked((int)message);
        if (code == 0 && target != 0 && kind is KeyDown or KeyUp or SystemKeyDown or SystemKeyUp &&
            GetForegroundWindow() == target)
        {
            var key = (Keys)Marshal.ReadInt32(data);
            if (HandleKey(key, kind is KeyUp or SystemKeyUp))
                return 1;
        }
        return CallNextHookEx(0, code, message, data);
    }

    private bool HandleKey(Keys key, bool released)
    {
        HeldKey flag = Map(key);
        if (flag == HeldKey.None)
            return false;
        bool alreadyDown = (down & flag) != 0;
        bool owned = (consumed & flag) != 0;
        if (released)
        {
            down &= ~flag;
            consumed &= ~flag;
            // Finish the chord before the action can minimize or re-create the window.
            if (owned)
                PostMessage(target, ShortcutMessage, (nint)key, 0);
            return owned;
        }
        down |= flag;
        if (alreadyDown)
            return owned;
        if ((flag & HeldKey.Shortcuts) == 0 || (down & HeldKey.Control) == 0 ||
            (down & HeldKey.Shift) == 0 || (down & HeldKey.OtherModifier) != 0)
            return false;
        consumed |= flag;
        return true;
    }

    private void ReadHeldKeys()
    {
        ReadOnlySpan<Keys> keys = [Keys.LShiftKey, Keys.RShiftKey, Keys.LControlKey, Keys.RControlKey,
            Keys.LMenu, Keys.RMenu, Keys.LWin, Keys.RWin, Keys.F, Keys.N];
        foreach (Keys key in keys)
        {
            if (GetAsyncKeyState((int)key) < 0)
                down |= Map(key);
        }
    }

    private static HeldKey Map(Keys key) => key switch
    {
        Keys.ShiftKey or Keys.LShiftKey => HeldKey.LeftShift,
        Keys.RShiftKey => HeldKey.RightShift,
        Keys.ControlKey or Keys.LControlKey => HeldKey.LeftControl,
        Keys.RControlKey => HeldKey.RightControl,
        Keys.Menu or Keys.LMenu => HeldKey.LeftAlt,
        Keys.RMenu => HeldKey.RightAlt,
        Keys.LWin => HeldKey.LeftWindows,
        Keys.RWin => HeldKey.RightWindows,
        Keys.F => HeldKey.FullScreen,
        Keys.N => HeldKey.Minimize,
        _ => HeldKey.None
    };

    private delegate nint HookProcedure(int code, nint message, nint data);

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW")]
    private static extern nint SetWindowsHookEx(int type, HookProcedure procedure, nint module, uint thread);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, int message, nint wParam, nint lParam);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);
}
