namespace Snapshot;

/// <summary>Small append-only diagnostic log at %LOCALAPPDATA%\Snapshot\capture.log (trimmed to ~256 KB).</summary>
internal static class CaptureLog
{
    private const long MaxBytes = 256 * 1024;

    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Snapshot");

    public static string FilePath => Path.Combine(Folder, "capture.log");

    public static void Write(string text)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var fi = new FileInfo(FilePath);
            if (fi.Exists && fi.Length > MaxBytes)
            {
                // Keep the newest half.
                string all = File.ReadAllText(FilePath);
                int cut = all.IndexOf("=== ", all.Length / 2, StringComparison.Ordinal);
                File.WriteAllText(FilePath, cut > 0 ? all.Substring(cut) : "");
            }
            File.AppendAllText(FilePath, text.EndsWith("\n") ? text : text + Environment.NewLine);
        }
        catch { /* logging must never break a capture */ }
    }
}
