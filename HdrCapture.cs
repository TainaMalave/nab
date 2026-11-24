using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Nab;

/// <summary>
/// HDR-aware capture. When Windows HDR is on, GDI (BitBlt) returns the desktop without tone mapping, so bright
/// scenes (games especially) come out blown out. For every monitor that is in HDR mode we instead use DXGI Desktop
/// Duplication (IDXGIOutput5::DuplicateOutput1) to grab the FP16 scRGB frame, tone-map it to 8-bit sRGB on the CPU
/// and paint it over the BitBlt result. Monitors in SDR mode keep the BitBlt pixels. Any failure on a monitor just
/// leaves the BitBlt pixels for that monitor, so this can only improve on the old behaviour.
///
/// COM is called through raw vtables (C# function pointers) to avoid any NuGet/interop assembly. Every vtable slot is
/// commented with the interface it belongs to; indices count IUnknown (0-2) first.
/// </summary>
internal static unsafe class HdrCapture
{
    // ---- constants --------------------------------------------------------------------------------
    private const int DXGI_FORMAT_R16G16B16A16_FLOAT = 10;
    private const int DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020 = 12; // HDR10 desktop
    private const int DXGI_MODE_ROTATION_UNSPECIFIED = 0, DXGI_MODE_ROTATION_IDENTITY = 1;
    private const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);
    private const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
    private const int D3D_DRIVER_TYPE_UNKNOWN = 0;
    private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    private const uint D3D11_SDK_VERSION = 7;
    private const uint D3D11_USAGE_STAGING = 3, D3D11_CPU_ACCESS_READ = 0x20000, D3D11_MAP_READ = 1;

    private static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid IID_IDXGIOutput5 = new("80a07424-ab52-42eb-833c-0c42fd282d98");
    private static readonly Guid IID_IDXGIOutput6 = new("068346e8-aaec-4b84-add7-137f513f77a1");
    private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    // ---- vtable slots -----------------------------------------------------------------------------
    // IUnknown
    private const int QueryInterface = 0, Release = 2;
    // IDXGIFactory1 : IDXGIFactory : IDXGIObject(3-6) ; IDXGIFactory 7-11 ; IDXGIFactory1: 12 EnumAdapters1, 13 IsCurrent
    private const int Factory1_EnumAdapters1 = 12;
    // IDXGIAdapter : IDXGIObject(3-6) ; 7 EnumOutputs, 8 GetDesc, 9 CheckInterfaceSupport
    private const int Adapter_EnumOutputs = 7;
    // IDXGIOutput : IDXGIObject(3-6) ; 7 GetDesc ... 18 GetFrameStatistics
    // IDXGIOutput1: 19 GetDisplayModeList1, 20 FindClosestMatchingMode1, 21 GetDisplaySurfaceData1, 22 DuplicateOutput
    // IDXGIOutput2: 23 SupportsOverlays ; IDXGIOutput3: 24 CheckOverlaySupport ; IDXGIOutput4: 25 CheckOverlayColorSpaceSupport
    // IDXGIOutput5: 26 DuplicateOutput1 ; IDXGIOutput6: 27 GetDesc1, 28 CheckHardwareCompositionSupport
    private const int Output5_DuplicateOutput1 = 26;
    private const int Output6_GetDesc1 = 27;
    // IDXGIOutputDuplication : IDXGIObject(3-6) ; 7 GetDesc, 8 AcquireNextFrame, 9 GetFrameDirtyRects,
    //   10 GetFrameMoveRects, 11 GetFramePointerShape, 12 MapDesktopSurface, 13 UnMapDesktopSurface, 14 ReleaseFrame
    private const int Dupl_AcquireNextFrame = 8, Dupl_ReleaseFrame = 14;
    // ID3D11Device : IUnknown ; 3 CreateBuffer, 4 CreateTexture1D, 5 CreateTexture2D
    private const int Device_CreateTexture2D = 5;
    // ID3D11DeviceContext : ID3D11DeviceChild(3 GetDevice, 4 GetPrivateData, 5 SetPrivateData, 6 SetPrivateDataInterface)
    //   7 VSSetConstantBuffers, 8 PSSetShaderResources, 9 PSSetShader, 10 PSSetSamplers, 11 VSSetShader,
    //   12 DrawIndexed, 13 Draw, 14 Map, 15 Unmap, ... 46 CopySubresourceRegion, 47 CopyResource
    private const int Ctx_Map = 14, Ctx_Unmap = 15, Ctx_CopyResource = 47;
    // ID3D11Texture2D : ID3D11Resource(7 GetType, 8 SetEvictionPriority, 9 GetEvictionPriority) ; 10 GetDesc
    private const int Tex2D_GetDesc = 10;

    private static void* Slot(IntPtr obj, int index) => (*(void***)obj)[index];

    private static void SafeRelease(ref IntPtr p)
    {
        if (p == IntPtr.Zero) return;
        ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(p, Release))(p);
        p = IntPtr.Zero;
    }

    private static int QI(IntPtr p, Guid iid, out IntPtr result)
    {
        IntPtr r = IntPtr.Zero;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, void*, void*, int>)Slot(p, QueryInterface))(p, &iid, &r);
        result = r;
        return hr;
    }

    // ---- structs (must be strictly blittable: no char, bool, string, or MarshalAs) -----------------
    // Sizes below are for x64 (IntPtr = 8), which is how this AnyCPU exe runs on 64-bit Windows.
    // `fixed char[N]` is NOT blittable (the marshaller treats char as ANSI/Unicode) and was the v3/v4 bug:
    // passing DXGI_OUTPUT_DESC1* to a function pointer threw MarshalDirectiveException on parameter #2.

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; } // 16 bytes

    /// <summary>DXGI_OUTPUT_DESC1: 152 bytes on x64 (DXGI_OUTPUT_DESC is 96; the extra fields are 52; then 4 bytes of trailing padding because the struct aligns to 8).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_OUTPUT_DESC1
    {
        public fixed ushort DeviceName[32]; // WCHAR[32] as ushort, not char (char is non-blittable)
        public RECT DesktopCoordinates;     // 16
        public int AttachedToDesktop;       // BOOL
        public int Rotation;
        public IntPtr Monitor;              // HMONITOR, 8 on x64
        public uint BitsPerColor;
        public int ColorSpace;
        public fixed float Primaries[8];    // Red[2], Green[2], Blue[2], White[2]
        public float MinLuminance, MaxLuminance, MaxFullFrameLuminance;
    }

    /// <summary>DXGI_OUTDUPL_FRAME_INFO: 48 bytes (PointerPosition is POINT+BOOL with no trailing pad).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_OUTDUPL_FRAME_INFO
    {
        public long LastPresentTime;
        public long LastMouseUpdateTime;
        public uint AccumulatedFrames;
        public int RectsCoalesced;
        public int ProtectedContentMaskedOut;
        public int PointerX, PointerY, PointerVisible;
        public uint TotalMetadataBufferSize;
        public uint PointerShapeBufferSize;
    }

    /// <summary>D3D11_TEXTURE2D_DESC (10 UINTs)</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_TEXTURE2D_DESC
    {
        public uint Width, Height, MipLevels, ArraySize;
        public int Format;
        public uint SampleCount, SampleQuality;
        public uint Usage, BindFlags, CPUAccessFlags, MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_MAPPED_SUBRESOURCE { public IntPtr pData; public uint RowPitch, DepthPitch; }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
        IntPtr featureLevels, uint numFeatureLevels, uint sdkVersion,
        out IntPtr device, out int featureLevel, out IntPtr context);

    private static string? s_interopCheck;

    /// <summary>Pin + Marshal.SizeOf for every native struct. Logged once so a Windows run confirms blittability.</summary>
    internal static string InteropSelfCheck()
    {
        if (s_interopCheck != null) return s_interopCheck;
        var sb = new System.Text.StringBuilder();
        sb.Append($"interop self-check (IntPtr={IntPtr.Size}, expect 8 on x64):");
        Check(sb, "RECT", 16, sizeof(RECT), Marshal.SizeOf<RECT>(), PinOk(default(RECT)));
        Check(sb, "DXGI_OUTPUT_DESC1", 152, sizeof(DXGI_OUTPUT_DESC1), Marshal.SizeOf<DXGI_OUTPUT_DESC1>(), PinOk(default(DXGI_OUTPUT_DESC1)));
        Check(sb, "DXGI_OUTDUPL_FRAME_INFO", 48, sizeof(DXGI_OUTDUPL_FRAME_INFO), Marshal.SizeOf<DXGI_OUTDUPL_FRAME_INFO>(), PinOk(default(DXGI_OUTDUPL_FRAME_INFO)));
        Check(sb, "D3D11_TEXTURE2D_DESC", 44, sizeof(D3D11_TEXTURE2D_DESC), Marshal.SizeOf<D3D11_TEXTURE2D_DESC>(), PinOk(default(D3D11_TEXTURE2D_DESC)));
        Check(sb, "D3D11_MAPPED_SUBRESOURCE", IntPtr.Size + 8, sizeof(D3D11_MAPPED_SUBRESOURCE), Marshal.SizeOf<D3D11_MAPPED_SUBRESOURCE>(), PinOk(default(D3D11_MAPPED_SUBRESOURCE)));
        return s_interopCheck = sb.ToString();
    }

    private static void Check(System.Text.StringBuilder sb, string name, int expected, int unsafeSize, int marshalSize, string pin)
        => sb.Append($" {name}: sizeof={unsafeSize} Marshal={marshalSize} expected={expected} pin={pin}" +
                     (unsafeSize == expected && marshalSize == expected && pin == "ok" ? ";" : " ** MISMATCH **;"));

    private static string PinOk<T>(T value) where T : struct
    {
        try { var h = System.Runtime.InteropServices.GCHandle.Alloc(value, System.Runtime.InteropServices.GCHandleType.Pinned); h.Free(); return "ok"; }
        catch (Exception ex) { return ex.GetType().Name + ": " + ex.Message; }
    }

        /// <summary>Last diagnostic (for debugging; not shown to the user).</summary>
    [ThreadStatic] private static long s_paintedPixels;

    public static string LastInfo { get; private set; } = "";

    /// <summary>
    /// For every HDR monitor intersecting <paramref name="screenRect"/>, replaces that part of <paramref name="bmp"/>
    /// (which already holds the BitBlt capture of screenRect) with a tone-mapped Desktop Duplication frame.
    /// Never throws.
    /// </summary>
    public static void OverlayHdrOutputs(Bitmap bmp, Rectangle screenRect)
    {
        var log = new System.Text.StringBuilder();
        log.AppendLine(InteropSelfCheck());
        log.AppendLine($"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} capture rect=({screenRect.Left},{screenRect.Top},{screenRect.Right},{screenRect.Bottom}) " +
                       $"bmp={bmp.Width}x{bmp.Height} dpiAwareness={Native.ThreadDpiAwareness()} toneMap={ToneMode} hdrBrightness={HdrBrightness:0.###} v{typeof(HdrCapture).Assembly.GetName().Version}");
        IntPtr factory = IntPtr.Zero;
        s_paintedPixels = 0;
        try
        {
            var iid = IID_IDXGIFactory1;
            if (CreateDXGIFactory1(ref iid, out factory) < 0 || factory == IntPtr.Zero) { log.AppendLine("  CreateDXGIFactory1 failed -> BitBlt only"); return; }

            for (uint a = 0; ; a++)
            {
                IntPtr adapter = IntPtr.Zero;
                int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, void*, int>)Slot(factory, Factory1_EnumAdapters1))(factory, a, &adapter);
                if (hr == DXGI_ERROR_NOT_FOUND || hr < 0) break;
                try
                {
                    for (uint o = 0; ; o++)
                    {
                        IntPtr output = IntPtr.Zero;
                        hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, void*, int>)Slot(adapter, Adapter_EnumOutputs))(adapter, o, &output);
                        if (hr == DXGI_ERROR_NOT_FOUND || hr < 0) break;
                        log.Append($"  output {a}/{o}:"); try { ProcessOutput(adapter, output, bmp, screenRect, log); }
                        catch (Exception ex) { log.AppendLine($"    EXCEPTION {ex.GetType().Name}: {ex.Message} -> BitBlt kept"); }
                        finally { SafeRelease(ref output); }
                    }
                }
                finally { SafeRelease(ref adapter); }
            }
        }
        catch (Exception ex) { log.AppendLine($"  EXCEPTION {ex.GetType().Name}: {ex.Message}"); }
        finally
        {
            SafeRelease(ref factory);
            long total = (long)screenRect.Width * screenRect.Height;
            log.AppendLine($"  summary: HDR path painted {s_paintedPixels} of {total} px ({100.0 * s_paintedPixels / Math.Max(1, total):0.#}%); the rest is GDI BitBlt");
            LastInfo = log.ToString();
            CaptureLog.Write(LastInfo);
        }
    }

    private static void ProcessOutput(IntPtr adapter, IntPtr output, Bitmap bmp, Rectangle screenRect, System.Text.StringBuilder log)
    {
        IntPtr out6 = IntPtr.Zero, out5 = IntPtr.Zero, device = IntPtr.Zero, ctx = IntPtr.Zero, dupl = IntPtr.Zero, staging = IntPtr.Zero;
        string step = "start";
        try
        {
            step = "IUnknown.QueryInterface(IDXGIOutput6)";
            if (QI(output, IID_IDXGIOutput6, out out6) < 0) { log.AppendLine(" no IDXGIOutput6 (Windows too old for HDR info) -> BitBlt"); return; } // pre-1803 => no HDR info
            step = "IDXGIOutput6.GetDesc1";
            DXGI_OUTPUT_DESC1 desc;
            int hrd = ((delegate* unmanaged[Stdcall]<IntPtr, void*, int>)Slot(out6, Output6_GetDesc1))(out6, &desc);
            if (hrd < 0) { log.AppendLine($" GetDesc1 0x{hrd:X8} -> BitBlt"); return; }
            string devName = new string((char*)desc.DeviceName, 0, 32).TrimEnd('\0');
            var dc = desc.DesktopCoordinates;
            bool isHdr = desc.ColorSpace == DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020;
            log.AppendLine($" {devName} coords=({dc.Left},{dc.Top},{dc.Right},{dc.Bottom}) attached={desc.AttachedToDesktop} rot={desc.Rotation} " +
                           $"colorSpace={desc.ColorSpace} bpc={desc.BitsPerColor} HDR={(isHdr ? "yes" : "no")} panelMaxNits={desc.MaxLuminance:0} panelMinNits={desc.MinLuminance:0.###}");
            if (desc.AttachedToDesktop == 0) { log.AppendLine("    not attached -> skip"); return; }
            if (!isHdr) { log.AppendLine("    SDR -> BitBlt (correct for SDR)"); return; }

            var outRect = Rectangle.FromLTRB(dc.Left, dc.Top, dc.Right, dc.Bottom);
            var hit = Rectangle.Intersect(outRect, screenRect);
            if (hit.Width <= 0 || hit.Height <= 0) { log.AppendLine("    outside capture rect -> skip"); return; }
            if (desc.Rotation != DXGI_MODE_ROTATION_IDENTITY && desc.Rotation != DXGI_MODE_ROTATION_UNSPECIFIED)
            { log.AppendLine("    FALLBACK: rotated HDR output not supported -> BitBlt"); return; }

            float sdrWhite = SdrWhiteLevel.Get(devName, out string whiteNote); // scRGB value of SDR white (1.0 == 80 nits)
            log.AppendLine($"    SDR white: {whiteNote} -> divisor {sdrWhite:0.###} ({sdrWhite * 80:0} nits); HdrBrightness={HdrBrightness:0.###}");

            step = "IUnknown.QueryInterface(IDXGIOutput5)";
            if (QI(output, IID_IDXGIOutput5, out out5) < 0) { log.AppendLine("    FALLBACK: no IDXGIOutput5 -> BitBlt"); return; }
            step = "D3D11CreateDevice";
            if (D3D11CreateDevice(adapter, D3D_DRIVER_TYPE_UNKNOWN, IntPtr.Zero, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                    IntPtr.Zero, 0, D3D11_SDK_VERSION, out device, out _, out ctx) < 0) { log.AppendLine("    FALLBACK: D3D11CreateDevice failed -> BitBlt"); return; }

            int fmt = DXGI_FORMAT_R16G16B16A16_FLOAT;
            step = "IDXGIOutput5.DuplicateOutput1";
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, void*, void*, int>)Slot(out5, Output5_DuplicateOutput1))
                (out5, device, 0, 1, &fmt, &dupl);
            if (hr < 0 || dupl == IntPtr.Zero) { log.AppendLine($"    FALLBACK: DuplicateOutput1 hr=0x{hr:X8} -> BitBlt"); return; }

            // Acquire frames. The first frame normally holds the full desktop, but can be empty (LastPresentTime == 0);
            // a running game presents constantly, so a couple of retries gets a real frame. A static desktop may time out
            // after the first frame; in that case we keep the copy we already made.
            D3D11_TEXTURE2D_DESC td = default;
            bool haveCopy = false, haveRealFrame = false;
            int attempts = 0;
            for (int attempt = 0; attempt < 6 && !haveRealFrame; attempt++)
            {
                attempts = attempt + 1;
                DXGI_OUTDUPL_FRAME_INFO fi;
                IntPtr res = IntPtr.Zero;
                step = $"IDXGIOutputDuplication.AcquireNextFrame(attempt {attempt})";
                hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, void*, void*, int>)Slot(dupl, Dupl_AcquireNextFrame))
                    (dupl, attempt == 0 ? 100u : 60u, &fi, &res);
                if (hr == DXGI_ERROR_WAIT_TIMEOUT) { if (haveCopy) break; else continue; }
                if (hr < 0) { log.AppendLine($"    AcquireNextFrame hr=0x{hr:X8} (attempt {attempt})"); break; }
                IntPtr tex = IntPtr.Zero;
                try
                {
                    if (QI(res, IID_ID3D11Texture2D, out tex) < 0) { log.AppendLine("    frame is not an ID3D11Texture2D"); break; }
                    if (staging == IntPtr.Zero)
                    {
                        step = "ID3D11Texture2D.GetDesc";
                        ((delegate* unmanaged[Stdcall]<IntPtr, void*, void>)Slot(tex, Tex2D_GetDesc))(tex, &td);
                        var sd = td;
                        sd.MipLevels = 1; sd.ArraySize = 1; sd.SampleCount = 1; sd.SampleQuality = 0;
                        sd.Usage = D3D11_USAGE_STAGING; sd.BindFlags = 0; sd.CPUAccessFlags = D3D11_CPU_ACCESS_READ; sd.MiscFlags = 0;
                        IntPtr st = IntPtr.Zero;
                        step = "ID3D11Device.CreateTexture2D";
                        hr = ((delegate* unmanaged[Stdcall]<IntPtr, void*, IntPtr, void*, int>)Slot(device, Device_CreateTexture2D))
                            (device, &sd, IntPtr.Zero, &st);
                        if (hr < 0) { log.AppendLine($"    CreateTexture2D hr=0x{hr:X8}"); break; }
                        staging = st;
                    }
                    step = "ID3D11DeviceContext.CopyResource";
                    ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)Slot(ctx, Ctx_CopyResource))(ctx, staging, tex);
                    haveCopy = true;
                    haveRealFrame = fi.LastPresentTime != 0;
                }
                finally
                {
                    SafeRelease(ref tex);
                    SafeRelease(ref res);
                    ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(dupl, Dupl_ReleaseFrame))(dupl);
                }
            }
            if (!haveCopy) { log.AppendLine($"    FALLBACK: no frame after {attempts} attempts -> BitBlt"); return; }
            if (td.Format != DXGI_FORMAT_R16G16B16A16_FLOAT) { log.AppendLine($"    FALLBACK: unexpected frame format {td.Format} (want 10 = R16G16B16A16_FLOAT) -> BitBlt"); return; }
            if (td.Width != outRect.Width || td.Height != outRect.Height) { log.AppendLine($"    FALLBACK: frame {td.Width}x{td.Height} != output {outRect.Width}x{outRect.Height} -> BitBlt"); return; }

            D3D11_MAPPED_SUBRESOURCE map;
            step = "ID3D11DeviceContext.Map";
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, void*, int>)Slot(ctx, Ctx_Map))
                (ctx, staging, 0, D3D11_MAP_READ, 0, &map);
            if (hr < 0) { log.AppendLine($"    FALLBACK: Map hr=0x{hr:X8} -> BitBlt"); return; }
            try
            {
                // Static desktop + empty first frame => all zeros. Don't paint black over a good BitBlt.
                if (!haveRealFrame && IsAllZero(map, (int)td.Width, (int)td.Height)) { log.AppendLine("    FALLBACK: empty (all-zero) frame -> BitBlt"); return; }
                log.AppendLine($"    duplication OK: frame {td.Width}x{td.Height} format={td.Format} (R16G16B16A16_FLOAT) attempts={attempts} realFrame={haveRealFrame}");
                s_paintedPixels += (long)hit.Width * hit.Height;
                step = "ToneMapInto";
                string stats = ToneMapInto(bmp, screenRect, outRect, hit, map, sdrWhite);
                log.AppendLine($"    painted HDR pixels at bmp ({hit.X - screenRect.X},{hit.Y - screenRect.Y}) {hit.Width}x{hit.Height}; {stats}");
            }
            finally
            {
                ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)Slot(ctx, Ctx_Unmap))(ctx, staging, 0);
            }
        }
        catch (Exception ex)
        {
            log.AppendLine($"    EXCEPTION {ex.GetType().Name} in {step}: {ex.Message} -> BitBlt kept");
        }
        finally
        {
            SafeRelease(ref staging);
            SafeRelease(ref dupl);
            SafeRelease(ref ctx);
            SafeRelease(ref device);
            SafeRelease(ref out5);
            SafeRelease(ref out6);
        }
    }

    private static bool IsAllZero(D3D11_MAPPED_SUBRESOURCE map, int w, int h)
    {
        for (int y = 0; y < h; y += 7)
        {
            ulong* row = (ulong*)((byte*)map.pData + (long)y * map.RowPitch);
            for (int x = 0; x < w; x += 7)
                if ((row[x] & 0x0000FFFFFFFFFFFFUL) != 0) return false; // ignore alpha
        }
        return true;
    }

    // ---- tone mapping -----------------------------------------------------------------------------

    private static float[]? s_half;      // FP16 bits -> float
    private static byte[]? s_srgb;       // linear [0,1] quantised -> sRGB byte
    private const int SrgbLutSize = 16384;

    private static void EnsureLuts()
    {
        if (s_half == null)
        {
            var t = new float[65536];
            for (int i = 0; i < 65536; i++) t[i] = HalfToFloat((ushort)i);
            s_half = t;
        }
        if (s_srgb == null)
        {
            var t = new byte[SrgbLutSize + 1];
            for (int i = 0; i <= SrgbLutSize; i++)
            {
                double l = (double)i / SrgbLutSize;
                double s = l <= 0.0031308 ? 12.92 * l : 1.055 * Math.Pow(l, 1 / 2.4) - 0.055;
                t[i] = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(s * 255)));
            }
            s_srgb = t;
        }
    }

    private static float HalfToFloat(ushort h)
    {
        int sign = (h >> 15) & 1, exp = (h >> 10) & 0x1F, mant = h & 0x3FF;
        float v;
        if (exp == 0) v = mant * (1f / 16777216f);                       // subnormal: mant * 2^-24
        else if (exp == 31) v = mant == 0 ? 65504f : 0f;                 // inf -> max, NaN -> 0
        else v = (float)((1 + mant / 1024.0) * Math.Pow(2, exp - 15));
        return sign == 1 ? -v : v;
    }

    /// <summary>"hue" (default) or "clip"; set with appSettings key HdrToneMap in nab.exe.config.</summary>
    public static string ToneMode
    {
        get
        {
            try
            {
                string? v = System.Configuration.ConfigurationManager.AppSettings["HdrToneMap"];
                if (string.Equals(v, "clip", StringComparison.OrdinalIgnoreCase)) return "clip";
            }
            catch { }
            return "hue";
        }
    }

    /// <summary>
    /// Linear brightness multiplier applied after dividing by SDR white and before hue/clip handling.
    /// Default 1.10. Read from appSettings key HdrBrightness and clamped to 0.5–2.0. HDR path only.
    /// </summary>
    public static float HdrBrightness
    {
        get
        {
            const float def = 1.10f;
            try
            {
                string? v = System.Configuration.ConfigurationManager.AppSettings["HdrBrightness"];
                if (string.IsNullOrWhiteSpace(v)) return def;
                if (!float.TryParse(v, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float n)
                    && !float.TryParse(v, out n))
                    return def;
                if (float.IsNaN(n) || float.IsInfinity(n)) return def;
                if (n < 0.5f) return 0.5f;
                if (n > 2.0f) return 2.0f;
                return n;
            }
            catch { return def; }
        }
    }

    /// <summary>
    /// scRGB (linear float16, Rec.709 primaries, 1.0 = 80 nits) -> 8-bit sRGB, matching what Snipping Tool / WGC do:
    /// 1. Divide by the SDR white level (SDRWhiteLevel/1000), so Windows' "SDR white" maps to 1.0.
    /// 2. Multiply by HdrBrightness (default 1.10). Then clamp negatives (wide-gamut colours outside sRGB).
    /// 3. Pixels whose channels are all &lt;= 1 are left untouched (no global normalisation, no midtone compression).
    ///    Pixels with a channel &gt; 1:
    ///      "hue"  (default): scale the pixel by 1/max(r,g,b), so it stays the same hue and saturation (bright orange
    ///                        stays orange instead of turning yellow/white);
    ///      "clip":           per-channel hard clip to 1 (exactly Snipping Tool's behaviour).
    /// 4. Linear -> sRGB transfer function, applied once, via a LUT.
    /// Returns a stats string for the log.
    /// </summary>
    private static string ToneMapInto(Bitmap bmp, Rectangle screenRect, Rectangle outRect, Rectangle hit,
        D3D11_MAPPED_SUBRESOURCE map, float sdrWhite)
    {
        EnsureLuts();
        float[] half = s_half!;
        byte[] srgb = s_srgb!;
        float brightness = HdrBrightness;
        float scale = brightness / sdrWhite;
        bool clip = ToneMode == "clip";
        byte* src0 = (byte*)map.pData;
        long pitch = map.RowPitch;

        var data = bmp.LockBits(new Rectangle(hit.X - screenRect.X, hit.Y - screenRect.Y, hit.Width, hit.Height),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        int rows = hit.Height;
        var rowMaxL = new float[rows];
        var rowMaxC = new float[rows];
        var rowOver = new int[rows];
        var rowNeg = new int[rows];
        try
        {
            byte* dst0 = (byte*)data.Scan0;
            long dstride = data.Stride;
            int sx0 = hit.X - outRect.X, sy0 = hit.Y - outRect.Y, w = hit.Width;
            System.Threading.Tasks.Parallel.For(0, rows, yy =>
            {
                ushort* s = (ushort*)(src0 + (sy0 + yy) * pitch) + sx0 * 4;
                byte* d = dst0 + yy * dstride;
                float maxL = 0, maxC = 0; int over = 0, neg = 0;
                for (int x = 0; x < w; x++, s += 4, d += 4)
                {
                    float r = half[s[0]] * scale, g = half[s[1]] * scale, b = half[s[2]] * scale;
                    if (r < 0 || g < 0 || b < 0) { neg++; if (r < 0) r = 0; if (g < 0) g = 0; if (b < 0) b = 0; }
                    float l = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                    if (l > maxL) maxL = l;
                    float m = r > g ? (r > b ? r : b) : (g > b ? g : b);
                    if (m > maxC) maxC = m;
                    if (m > 1f)
                    {
                        over++;
                        if (clip) { if (r > 1f) r = 1f; if (g > 1f) g = 1f; if (b > 1f) b = 1f; }
                        else { float k = 1f / m; r *= k; g *= k; b *= k; }
                    }
                    d[0] = srgb[Lut(b)]; d[1] = srgb[Lut(g)]; d[2] = srgb[Lut(r)]; d[3] = 255;
                }
                rowMaxL[yy] = maxL; rowMaxC[yy] = maxC; rowOver[yy] = over; rowNeg[yy] = neg;
            });
        }
        finally { bmp.UnlockBits(data); }

        long total = (long)hit.Width * hit.Height, overSum = 0, negSum = 0;
        float mL = 0, mC = 0;
        for (int i = 0; i < rows; i++)
        {
            if (rowMaxL[i] > mL) mL = rowMaxL[i];
            if (rowMaxC[i] > mC) mC = rowMaxC[i];
            overSum += rowOver[i]; negSum += rowNeg[i];
        }
        return $"hdrBrightness={brightness:0.###} maxLuminance={mL:0.###}xSDRwhite-after-boost ({mL / brightness * sdrWhite * 80:0} nits raw, {mL * sdrWhite * 80:0} after boost) maxChannel={mC:0.###} " +
               $"pixelsAboveSDRwhite={100.0 * overSum / Math.Max(1, total):0.##}% outOfGamut={100.0 * negSum / Math.Max(1, total):0.##}%";
    }

    private static int Lut(float v)
    {
        if (!(v > 0f)) return 0; // also catches NaN
        if (v >= 1f) return SrgbLutSize;
        return (int)(v * SrgbLutSize + 0.5f);
    }

    // ---- SDR white level ---------------------------------------------------------------------------

    /// <summary>Looks up Windows' "SDR content brightness" for a GDI device name (\\.\DISPLAYn) via DisplayConfig.</summary>
    private static class SdrWhiteLevel
    {
        private const uint QDC_ONLY_ACTIVE_PATHS = 2;
        private const int DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
        private const int DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL = 11;
        private const int PathSize = 72, ModeSize = 64; // sizeof(DISPLAYCONFIG_PATH_INFO), sizeof(DISPLAYCONFIG_MODE_INFO)
        public const float Default = 2.5f;               // 200 nits, close to Windows' default slider position

        [DllImport("user32.dll")]
        private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);
        [DllImport("user32.dll")]
        private static extern int QueryDisplayConfig(uint flags, ref uint numPaths, byte* paths, ref uint numModes, byte* modes, IntPtr topologyId);
        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(byte* packet);

        public static float Get(string gdiName, out string note)
        {
            note = "default (not found in DisplayConfig)";
            try
            {
                int e0 = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint np, out uint nm);
                if (e0 != 0 || np == 0) { note = $"default (GetDisplayConfigBufferSizes err={e0})"; return Default; }
                var paths = new byte[np * PathSize];
                var modes = new byte[nm * ModeSize + ModeSize];
                fixed (byte* pp = paths) fixed (byte* pm = modes)
                {
                    int e1 = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, pp, ref nm, pm, IntPtr.Zero);
                    if (e1 != 0) { note = $"default (QueryDisplayConfig err={e1})"; return Default; }
                    byte* name = stackalloc byte[84];
                    byte* wl = stackalloc byte[24];
                    for (int i = 0; i < np; i++)
                    {
                        byte* path = pp + i * PathSize;
                        // DISPLAYCONFIG_PATH_SOURCE_INFO at 0: LUID adapterId (8), UINT32 id (4) ...
                        // DISPLAYCONFIG_PATH_TARGET_INFO at 20: LUID adapterId (8), UINT32 id (4) ...
                        long srcLuid = *(long*)(path + 0); uint srcId = *(uint*)(path + 8);
                        long tgtLuid = *(long*)(path + 20); uint tgtId = *(uint*)(path + 28);

                        // DISPLAYCONFIG_SOURCE_DEVICE_NAME: header(20) + WCHAR viewGdiDeviceName[32]
                        for (int k = 0; k < 84; k++) name[k] = 0;
                        *(int*)(name + 0) = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
                        *(int*)(name + 4) = 84;
                        *(long*)(name + 8) = srcLuid;
                        *(uint*)(name + 16) = srcId;
                        if (DisplayConfigGetDeviceInfo(name) != 0) continue;
                        string n = new string((char*)(name + 20)).TrimEnd('\0');
                        if (!string.Equals(n, gdiName, StringComparison.OrdinalIgnoreCase)) continue;

                        // DISPLAYCONFIG_SDR_WHITE_LEVEL: header(20) + ULONG SDRWhiteLevel (1000 == 80 nits)
                        for (int k = 0; k < 24; k++) wl[k] = 0;
                        *(int*)(wl + 0) = DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL;
                        *(int*)(wl + 4) = 24;
                        *(long*)(wl + 8) = tgtLuid;
                        *(uint*)(wl + 16) = tgtId;
                        int e2 = DisplayConfigGetDeviceInfo(wl);
                        if (e2 != 0) { note = $"default (GET_SDR_WHITE_LEVEL err={e2})"; return Default; }
                        uint level = *(uint*)(wl + 20);
                        // SDRWhiteLevel: 1000 == 80 nits, so nits = level/1000*80 and the scRGB value of SDR white = level/1000.
                        if (level < 1000 || level > 12000) { note = $"default (raw={level} out of range)"; return Default; } // sanity: 80..960 nits
                        note = $"raw SDRWhiteLevel={level}";
                        return level / 1000f;
                    }
                }
            }
            catch (Exception ex) { note = $"default ({ex.GetType().Name}: {ex.Message})"; }
            return Default;
        }
    }
}
