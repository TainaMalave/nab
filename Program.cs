namespace Snapshot;

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

        using var mutex = new Mutex(true, @"Local\Snapshot.Prototype.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            MessageBox.Show("Snapshot is already running. Look for its icon in the system tray.",
                "Snapshot", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.Run(new TrayApp());
        GC.KeepAlive(mutex);
    }
}
