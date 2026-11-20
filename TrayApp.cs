using System.Diagnostics;
using System.Drawing.Imaging;

namespace Snapshot;

/// <summary>Tray icon + hotkeys + capture flow + thumbnail stack. No main window.</summary>
internal sealed class TrayApp : ApplicationContext
{
    private const int HotkeyRegion = 1, HotkeyFull = 2;

    private readonly NotifyIcon _tray;
    private readonly HotkeyWindow _hotkeys = new();
    private readonly List<ThumbnailForm> _thumbs = new(); // oldest first (bottom of the stack)
    private bool _busy;

    public TrayApp()
    {
        string regionKeys = RegisterWithFallback(HotkeyRegion, Keys.D2);
        string fullKeys = RegisterWithFallback(HotkeyFull, Keys.D1);
        _hotkeys.Pressed += id => _ = id == HotkeyRegion ? CaptureRegionAsync(0) : CaptureFullAsync(0);

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Capture region", null, (_, _) => _ = CaptureRegionAsync(250))
            { ShortcutKeyDisplayString = regionKeys });
        menu.Items.Add(new ToolStripMenuItem("Capture full screen", null, (_, _) => _ = CaptureFullAsync(250))
            { ShortcutKeyDisplayString = fullKeys });
        menu.Items.Add("Open Snapshots folder", null, (_, _) => OpenFolder());
        menu.Items.Add("Open capture log", null, (_, _) => OpenLog());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _tray = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Snapshot",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) _ = CaptureRegionAsync(150); };

        bool ok = regionKeys != "" && fullKeys != "";
        _tray.ShowBalloonTip(4000, "Snapshot is running",
            ok ? $"{regionKeys}: capture region\n{fullKeys}: capture screen"
               : "Some hotkeys are in use by another app. Use the tray icon menu instead.",
            ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
    }

    /// <summary>Try Ctrl+Shift+key; if another app owns it, fall back to Ctrl+Alt+Shift+key. Returns the label or "".</summary>
    private string RegisterWithFallback(int id, Keys key)
    {
        string k = ((int)key - (int)Keys.D0).ToString();
        if (_hotkeys.Register(id, Native.MOD_CONTROL | Native.MOD_SHIFT, key)) return $"Ctrl+Shift+{k}";
        if (_hotkeys.Register(id, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_SHIFT, key)) return $"Ctrl+Alt+Shift+{k}";
        return "";
    }

    private static Icon LoadIcon()
    {
        using var s = typeof(TrayApp).Assembly.GetManifestResourceStream("Snapshot.app.ico");
        return s != null ? new Icon(s, SystemInformation.SmallIconSize) : SystemIcons.Application;
    }

    // ---- capture flow -----------------------------------------------------------------------------

    private async Task CaptureRegionAsync(int delayMs)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            await HideThumbsAsync(delayMs);
            var vs = SystemInformation.VirtualScreen; // physical px (per-monitor v2), may have negative origin
            Bitmap? result = null;
            using (var screen = ScreenCapture.Grab(vs))
            using (var overlay = new RegionOverlay(screen, vs))
            {
                if (overlay.ShowDialog() == DialogResult.OK)
                    result = screen.Clone(overlay.Selection, PixelFormat.Format24bppRgb);
            }
            ShowThumbs();
            if (result != null) Deliver(result);
        }
        catch (Exception ex) { ShowThumbs(); Error(ex); }
        finally { _busy = false; }
    }

    private async Task CaptureFullAsync(int delayMs)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            await HideThumbsAsync(delayMs);
            var bounds = Screen.FromPoint(Cursor.Position).Bounds; // the monitor under the mouse
            Bitmap shot;
            using (var raw = ScreenCapture.Grab(bounds))
                shot = raw.Clone(new Rectangle(Point.Empty, raw.Size), PixelFormat.Format24bppRgb);
            ShowThumbs();
            Deliver(shot);
        }
        catch (Exception ex) { ShowThumbs(); Error(ex); }
        finally { _busy = false; }
    }

    /// <summary>Hide existing thumbnails (so they aren't in the shot) and let menus/DWM settle.</summary>
    private async Task HideThumbsAsync(int delayMs)
    {
        bool any = _thumbs.Count > 0;
        foreach (var t in _thumbs) t.SetHidden(true);
        int wait = Math.Max(delayMs, any ? 120 : 0);
        if (wait > 0) await Task.Delay(wait);
    }

    private void ShowThumbs()
    {
        foreach (var t in _thumbs) if (!t.IsDisposed && !t.Visible) t.SetHidden(false);
    }

    private void Deliver(Bitmap shot)
    {
        string path = "";
        try { path = ScreenCapture.SavePng(shot); }
        catch (Exception ex) { Error(ex, "Couldn't save the PNG"); }
        try { ScreenCapture.CopyToClipboard(shot); }
        catch (Exception ex) { Error(ex, "Couldn't copy to the clipboard"); }
        AddThumbnail(shot, path);
    }

    // ---- thumbnail stack --------------------------------------------------------------------------

    private void AddThumbnail(Bitmap shot, string path)
    {
        var wa = Screen.PrimaryScreen!.WorkingArea;
        float s = Native.ScaleAt(new Point(wa.Right - 1, wa.Bottom - 1));
        var thumb = new ThumbnailForm(shot, path, s);
        thumb.FormClosed += (_, _) =>
        {
            _thumbs.Remove(thumb); // WinForms disposes a closed modeless form itself
            LayoutThumbs();
        };
        _thumbs.Add(thumb);
        // Drop the oldest ones if the stack would run off the top of the screen.
        while (_thumbs.Count > 1 && StackHeight(s) > wa.Height)
        {
            var oldest = _thumbs[0];
            _thumbs.RemoveAt(0);
            oldest.Close();
        }
        LayoutThumbs();
        thumb.ShowNoActivate();
    }

    private int StackHeight(float s) =>
        (int)(16 * s) * 2 + _thumbs.Sum(t => t.Height) + (int)(10 * s) * (_thumbs.Count - 1);

    private void LayoutThumbs()
    {
        var wa = Screen.PrimaryScreen!.WorkingArea;
        float s = Native.ScaleAt(new Point(wa.Right - 1, wa.Bottom - 1));
        int margin = (int)(16 * s), gap = (int)(10 * s);
        int y = wa.Bottom - margin;
        foreach (var t in _thumbs) // oldest at the bottom, newer ones stack upward
        {
            y -= t.Height;
            t.Location = new Point(wa.Right - margin - t.Width, y);
            y -= gap;
        }
    }

    // ---- misc -------------------------------------------------------------------------------------

    private static void OpenFolder()
    {
        Directory.CreateDirectory(ScreenCapture.Folder);
        Process.Start("explorer.exe", $"\"{ScreenCapture.Folder}\"");
    }

    private void OpenLog()
    {
        try
        {
            if (!File.Exists(CaptureLog.FilePath))
                CaptureLog.Write("(no captures logged yet; take a screenshot, then open this log again)");
            Process.Start("notepad.exe", $"\"{CaptureLog.FilePath}\"");
        }
        catch (Exception ex) { Error(ex, "Couldn't open the log"); }
    }

    private void Error(Exception ex, string title = "Capture failed") =>
        _tray.ShowBalloonTip(4000, title, ex.Message, ToolTipIcon.Error);

    protected override void ExitThreadCore()
    {
        foreach (var t in _thumbs.ToArray()) t.Close();
        _hotkeys.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        base.ExitThreadCore();
    }
}
