using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace VpnRdpTray;

internal sealed class OwnedProcess : IDisposable
{
    private readonly SafeFileHandle job;
    internal Process Process { get; }

    private OwnedProcess(SafeFileHandle job, Process process)
    {
        this.job = job;
        Process = process;
    }

    internal static OwnedProcess Start(string executable, string path, bool minimized)
    {
        var job = CreateJobObject(0, null);
        var limits = new JobExtendedLimits { Basic = new JobBasicLimits { Flags = 0x2000 } };
        Process? process = null;
        try
        {
            if (job.IsInvalid || !SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf<JobExtendedLimits>()))
                throw new Win32Exception();
            var startup = new StartupInfo
            {
                Size = Marshal.SizeOf<StartupInfo>(),
                Flags = 1,
                Show = minimized ? (short)7 : (short)1
            };
            var commandLine = new StringBuilder($"\"{executable}\" \"{path}\"");
            if (!CreateProcess(executable, commandLine, 0, 0, false, 4, 0, Environment.SystemDirectory,
                    ref startup, out var native))
                throw new Win32Exception();
            using var child = new SafeFileHandle(native.Process, true);
            using var thread = new SafeFileHandle(native.Thread, true);
            if (!AssignProcessToJobObject(job, child))
            {
                var error = new Win32Exception();
                TerminateProcess(child, 1);
                throw error;
            }
            process = Process.GetProcessById(native.Pid);
            if (ResumeThread(thread) == uint.MaxValue)
                throw new Win32Exception();
            return new OwnedProcess(job, process);
        }
        catch
        {
            job.Dispose();
            process?.Dispose();
            throw;
        }
    }

    internal void Reveal()
    {
        Process.Refresh();
        if (Process.HasExited)
            return;
        nint firstWindow = 0;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out int pid);
            if (pid != Process.Id || !IsWindowVisible(window))
                return true;
            if (firstWindow == 0 || GetWindow(window, 4) != 0)
                firstWindow = window;
            return true;
        }, 0);
        if (firstWindow == 0)
            return;
        ShowWindowAsync(firstWindow, 9);
        SetForegroundWindow(firstWindow);
    }

    public void Dispose()
    {
        // Terminate only the client started in this job. Windows also does this if the tray process dies.
        job.Dispose();
        try { Process.WaitForExit(2000); }
        catch (InvalidOperationException) { }
        Process.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobBasicLimits
    {
        public long PerProcess, PerJob;
        public uint Flags;
        public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ProcessLimit;
        public nuint Affinity;
        public uint Priority, Scheduling;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobExtendedLimits
    {
        public JobBasicLimits Basic;
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public nint Reserved, Desktop, Title;
        public uint X, Y, Width, Height, XChars, YChars, Fill, Flags;
        public short Show, ReservedSize;
        public nint ReservedPointer, Input, Output, Error;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo
    {
        public nint Process, Thread;
        public int Pid, Tid;
    }

    private delegate bool WindowCallback(nint window, nint argument);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(nint attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref JobExtendedLimits info, int size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string executable, StringBuilder command, nint processAttributes, nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, nint environment, string directory,
        ref StartupInfo startup, out ProcessInfo process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeFileHandle thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeFileHandle process, uint exitCode);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(WindowCallback callback, nint argument);

    [DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(nint window, out int processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint window, uint command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(nint window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);
}
