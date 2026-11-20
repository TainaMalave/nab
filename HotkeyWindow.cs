namespace Snapshot;

/// <summary>Hidden message-only window that owns the global hotkeys and raises <see cref="Pressed"/>.</summary>
internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private readonly List<int> _ids = new();
    public event Action<int>? Pressed;

    public HotkeyWindow() => CreateHandle(new CreateParams { Parent = Native.HWND_MESSAGE });

    public bool Register(int id, uint modifiers, Keys key)
    {
        bool ok = Native.RegisterHotKey(Handle, id, modifiers | Native.MOD_NOREPEAT, (uint)key);
        if (ok) _ids.Add(id);
        return ok;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY)
        {
            Pressed?.Invoke((int)m.WParam);
            return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        foreach (var id in _ids) Native.UnregisterHotKey(Handle, id);
        _ids.Clear();
        DestroyHandle();
    }
}
