using System.Runtime.InteropServices;

namespace VpnRdpTray;

internal sealed class RdpActivitySchedule(long startedAt, bool? focused)
{
    private const long QuietMilliseconds = 60_000;
    private static readonly Keys[] HeldInputs =
    [
        Keys.ShiftKey, Keys.ControlKey, Keys.Menu, Keys.LWin, Keys.RWin,
        Keys.LButton, Keys.RButton, Keys.MButton, Keys.XButton1, Keys.XButton2
    ];

    private bool? lastFocus = focused;
    private long focusChangedAt = startedAt;
    private long lastAttemptAt = startedAt;

    internal void ObserveFocus(bool? hasFocus, long now)
    {
        if (lastFocus == hasFocus)
            return;
        lastFocus = hasFocus;
        focusChangedAt = now;
    }

    internal bool CanSend(bool? hasFocus, long now, int? localIdleMilliseconds)
    {
        ObserveFocus(hasFocus, now);
        if (hasFocus is null || now - focusChangedAt < QuietMilliseconds || now - lastAttemptAt < QuietMilliseconds)
            return false;

        return hasFocus == false || localIdleMilliseconds >= QuietMilliseconds;
    }

    internal void RecordAttempt(long now) => lastAttemptAt = now;

    internal static (uint Timestamp, int IdleMilliseconds)? ReadLocalInput()
    {
        var info = new LastInputInfo { Size = sizeof(uint) * 2 };
        if (!GetLastInputInfo(ref info))
            return null;

        // LASTINPUTINFO shares the Win32 tick clock. .NET 11's TickCount64 excludes sleep.
        return (info.Time, InputElapsed(GetTickCount(), info.Time));
    }

    // A signed difference handles DWORD rollover and rejects timestamps ahead of the sample.
    internal static int InputElapsed(uint systemTick, uint lastInput) => unchecked((int)(systemTick - lastInput));

    internal static bool HasHeldModifierOrButton()
    {
        foreach (Keys key in HeldInputs)
            if (GetAsyncKeyState((int)key) < 0)
                return true;
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetTickCount();
}
