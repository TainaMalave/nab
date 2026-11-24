using System.Drawing.Imaging;

namespace Nab;

internal static class ScreenCapture
{
    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "nab");

    /// <summary>Grabs a rectangle of the desktop in physical screen pixels.</summary>
    public static Bitmap Grab(Rectangle screenRect)
    {
        var bmp = new Bitmap(screenRect.Width, screenRect.Height, PixelFormat.Format32bppPArgb);
        // .NET Framework's CopyFromScreen rejects SourceCopy|CaptureBlt, so call BitBlt directly.
        using (var g = Graphics.FromImage(bmp))
        {
            IntPtr dst = g.GetHdc();
            IntPtr src = GetDC(IntPtr.Zero);
            try
            {
                if (!BitBlt(dst, 0, 0, screenRect.Width, screenRect.Height, src,
                        screenRect.Left, screenRect.Top, SRCCOPY | CAPTUREBLT))
                    throw new System.ComponentModel.Win32Exception();
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, src);
                g.ReleaseHdc(dst);
            }
        }
        // HDR monitors: GDI returns un-tone-mapped pixels (blown-out games). Replace those parts with a
        // tone-mapped DXGI Desktop Duplication frame. SDR monitors keep the BitBlt pixels. Never throws.
        HdrCapture.OverlayHdrOutputs(bmp, screenRect);
        return bmp;
    }

    private const int SRCCOPY = 0x00CC0020;
    private const int CAPTUREBLT = 0x40000000;

    [System.Runtime.InteropServices.DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int w, int h,
        IntPtr hdcSrc, int xSrc, int ySrc, int rop);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    public static string SavePng(Bitmap bmp)
    {
        Directory.CreateDirectory(Folder);
        string stem = $"nab {DateTime.Now:yyyy-MM-dd 'at' HH.mm.ss}";
        string path = Path.Combine(Folder, stem + ".png");
        for (int i = 2; File.Exists(path); i++)
            path = Path.Combine(Folder, $"{stem} ({i}).png");
        bmp.Save(path, ImageFormat.Png);
        return path;
    }

    /// <summary>Puts the image on the clipboard as both a bitmap and PNG (PNG keeps quality in Slack, browsers, etc.).</summary>
    public static void CopyToClipboard(Bitmap bmp)
    {
        var data = new DataObject();
        data.SetData(DataFormats.Bitmap, true, bmp);
        var png = new MemoryStream();
        bmp.Save(png, ImageFormat.Png);
        data.SetData("PNG", false, png);
        // copy:true flushes so the image survives if the app exits; retries handle a briefly locked clipboard.
        Clipboard.SetDataObject(data, true, 10, 100);
    }
}
