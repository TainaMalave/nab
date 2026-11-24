using System.Globalization;

namespace Snapshot;

/// <summary>
/// The editor's last tool, colours, size and toggles, kept in %LOCALAPPDATA%\Snapshot\editor.ini so they survive a
/// restart. Plain key=value lines. Reading or writing never throws: a missing or broken file just means defaults.
/// </summary>
internal static class EditorSettings
{
    public static string FilePath => Path.Combine(CaptureLog.Folder, "editor.ini");

    public static Dictionary<string, string> Load()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(FilePath)) return d;
            foreach (var raw in File.ReadAllLines(FilePath))
            {
                int eq = raw.IndexOf('=');
                if (eq > 0) d[raw.Substring(0, eq).Trim()] = raw.Substring(eq + 1).Trim();
            }
        }
        catch { /* defaults */ }
        return d;
    }

    public static void Save(IEnumerable<KeyValuePair<string, string>> values)
    {
        try
        {
            Directory.CreateDirectory(CaptureLog.Folder);
            File.WriteAllLines(FilePath, values.Select(kv => kv.Key + "=" + kv.Value));
        }
        catch { /* not worth bothering the user about */ }
    }

    public static string ColorToString(Color c) => "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");

    public static bool TryColor(Dictionary<string, string> d, string key, out Color c)
    {
        c = Color.Empty;
        if (!d.TryGetValue(key, out var s) || s.Length != 7 || s[0] != '#') return false;
        if (!int.TryParse(s.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb)) return false;
        c = Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
        return true;
    }

    public static bool TryBool(Dictionary<string, string> d, string key, out bool b)
    {
        b = false;
        return d.TryGetValue(key, out var s) && bool.TryParse(s, out b);
    }

    public static bool TryInt(Dictionary<string, string> d, string key, out int i)
    {
        i = 0;
        return d.TryGetValue(key, out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out i);
    }
}
