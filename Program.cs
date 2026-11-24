namespace Nab;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Must run before any window is created. Per-monitor v2 => all coordinates we see
        // (Screen.Bounds, Cursor.Position, mouse events) are physical pixels, so captures are crisp.
        // The manifest already requests it; this is a fallback in case the manifest is ignored.
        Native.EnablePerMonitorDpi();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var mutex = new Mutex(true, @"Local\nab.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            MessageBox.Show("nab is already running. Look for its icon in the system tray.",
                "nab", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // nab used to be called Snapshot. Running both would fight over the same hotkeys.
        if (Mutex.TryOpenExisting(@"Local\Snapshot.Prototype.SingleInstance", out var old))
        {
            old.Dispose();
            MessageBox.Show("The older Snapshot app is still running. Exit it from its tray icon, then start nab again.",
                "nab", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.Run(new TrayApp());
        GC.KeepAlive(mutex);
    }
}
