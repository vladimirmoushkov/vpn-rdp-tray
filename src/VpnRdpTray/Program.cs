using System.ComponentModel;

namespace VpnRdpTray;

internal static class Program
{
    private static string ObjectName => $"Global\\VpnRdpTray.{MachineSetup.UserSid}";

    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args is ["--check-activex"])
            return RdpPrototype.CheckLocalControl();
        using var mutex = new Mutex(false, ObjectName);
        if (args is ["--shutdown"] or ["--uninstall"])
        {
            Signal(".Stop");
            if (!Take(mutex, TimeSpan.FromSeconds(25)))
                return 1;
            try
            {
                if (args[0] == "--uninstall")
                {
                    try { MachineSetup.RemoveAsync().GetAwaiter().GetResult(); }
                    catch (SetupException ex)
                    {
                        MessageBox.Show(ex.Message, "VPN Remote Desktop uninstall", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return 1;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
                    {
                        MessageBox.Show("Windows could not remove the app’s OpenVPN launcher or hosts mapping.",
                            "VPN Remote Desktop uninstall", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return 1;
                    }
                }
                return 0;
            }
            finally { mutex.ReleaseMutex(); }
        }

        bool openSetup = args is ["--setup"];
        bool openStatus = args is ["--status"];
        if (!Take(mutex, TimeSpan.Zero))
        {
            if (openSetup)
                Signal(".Show");
            else if (openStatus)
                Signal(".Status");
            return 0;
        }

        try
        {
            using var stop = new EventWaitHandle(false, EventResetMode.AutoReset, ObjectName + ".Stop");
            using var show = new EventWaitHandle(false, EventResetMode.AutoReset, ObjectName + ".Show");
            using var status = new EventWaitHandle(false, EventResetMode.AutoReset, ObjectName + ".Status");
            using var synchronization = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(synchronization);
            using var tray = new TrayApp(synchronization, stop, show, openSetup, openStatus);
            var statusRegistration = ThreadPool.RegisterWaitForSingleObject(status,
                (_, _) => synchronization.Post(_ => tray.ShowStatus(), null), null, Timeout.Infinite, false);
            try { Application.Run(tray); }
            finally { statusRegistration.Unregister(null); }
            return 0;
        }
        finally { mutex.ReleaseMutex(); }
    }

    private static bool Take(Mutex mutex, TimeSpan timeout)
    {
        try { return mutex.WaitOne(timeout); }
        catch (AbandonedMutexException) { return true; }
    }

    private static void Signal(string suffix)
    {
        if (!EventWaitHandle.TryOpenExisting(ObjectName + suffix, out var signal))
            return;
        using (signal) signal.Set();
    }

}
