# Snapshot (prototype v7)

A tiny CleanShot-style screenshot tool for Windows 10/11. It's one small `Snapshot.exe` (about 90 KB) built for .NET Framework 4.8, which ships with Windows 10 (1903+) and 11, so there's nothing to install. `Snapshot.exe.config` is optional: the exe runs without it.

## Run
1. Double-click `Snapshot.exe`. If SmartScreen shows "Windows protected your PC" (the exe is unsigned), click **More info**, then **Run anyway**.
2. A blue icon appears in the system tray (it may be under the `^` overflow arrow). No window opens.
3. To quit, right-click the tray icon and choose **Exit**. To remove it, delete the exe. It writes no settings or registry entries (only your screenshots in `Pictures\Snapshots` and a small log in `%LOCALAPPDATA%\Snapshot`).

## Hotkeys
| Action | Hotkey | Fallback if another app owns it |
|---|---|---|
| Capture region | **Ctrl+Shift+2** | Ctrl+Alt+Shift+2 |
| Capture full screen (monitor under the mouse) | **Ctrl+Shift+1** | Ctrl+Alt+Shift+1 |

The startup notification and the tray menu show which hotkeys were actually registered.
Left-clicking the tray icon also starts a region capture.
Note: while Snapshot runs, these hotkeys are global, so Excel's Ctrl+Shift+1 and Ctrl+Shift+2 number-format shortcuts won't reach Excel.

## Region capture
The screen freezes and dims across all monitors. Drag a rectangle (the live size shows in pixels).
**Esc** or **right-click** cancels.

## After every capture
* The image is copied to the clipboard (as a bitmap and as PNG).
* A PNG is saved to `Pictures\Snapshots\Snapshot YYYY-MM-DD at HH.MM.SS.png`.
* A **floating thumbnail** appears at the bottom-right. It never takes focus, so you can keep typing.
  * It disappears after about 6 seconds. Hovering keeps it open.
  * On hover you get **Edit**, **Copy**, **Save As** (PNG or JPG), **Folder** (opens Explorer with the file selected), and **✕**.
  * **Double-click** the thumbnail (or click **Edit**) to open the annotation editor.
  * **Drag the thumbnail** into Slack, Discord, an email, a browser upload box, or an Explorer folder to drop the PNG file there.
  * New captures stack upward. If the stack gets too tall, the oldest is removed.

## Annotation editor (new in v7)
Opens on the monitor the screenshot came from, at 100% if it fits, otherwise zoomed to fit.

| Tool | Key | Notes |
|---|---|---|
| Arrow | **A** | Drag. Hold **Shift** to snap to 45°. |
| Rectangle | **R** | Drag. Hold **Shift** for a square. |
| Ellipse | **E** | Drag. Hold **Shift** for a circle. |
| Text | **T** | Click and type. **Enter** = new line, **Esc** or click elsewhere = finish. Click existing text to edit it. |

* **Size:** three choices (keys **1**, **2**, **3**). Sets line thickness, or font size for text.
* **Fill (F):** filled rectangles/ellipses, or a coloured background behind text (on by default for text, so it reads on busy screenshots).
* **Colours:** red, yellow, green, blue, black, white, plus a custom colour picker. Changing colour, size, or fill while typing restyles that text.
* The editor remembers your last tool, colour, size, and fill until Snapshot exits.
* **Undo / redo:** Ctrl+Z, Ctrl+Y (or Ctrl+Shift+Z).
* **Zoom:** Ctrl+mouse wheel, Ctrl+plus/minus, **Ctrl+1** = 100%, **Ctrl+0** = fit. Click the % in the toolbar to switch between them. Scroll with the wheel (Shift+wheel sideways); pan with the middle mouse button or **Space**+drag.
* **Copy** (Ctrl+C) copies the annotated image.
* **Save** (Ctrl+S) saves over the original PNG in `Pictures\Snapshots`.
* **Save As** (Ctrl+Shift+S) writes a new PNG or JPG and leaves the original alone.
* **Done** (Ctrl+Enter) saves over the original if anything changed, copies, and closes.
* Closing with unsaved changes asks first. Exiting Snapshot from the tray also asks for each open editor.
* On a narrow window (like a portrait monitor) the toolbar wraps onto two rows.

To open the editor automatically after every capture (instead of showing the thumbnail), open `Snapshot.exe.config` in Notepad, set `OpenEditorAfterCapture` to `true`, and restart Snapshot. It's off by default.

## HDR displays (new in v3)
With Windows HDR turned on, the classic GDI screen grab returns the desktop without tone mapping, so games (World of Warcraft, etc.) came out blown out and washed out.
v3 detects which monitors are in HDR mode and captures those through DXGI Desktop Duplication in 16-bit floating-point (scRGB). It then tone-maps the image to normal 8-bit sRGB:
* Windows' **SDR content brightness** setting maps to white, the same conversion Snipping Tool uses. Everything at or below that brightness is left exactly as-is, so contrast and saturation match Snipping Tool.
* v4 handles colors brighter than SDR white two ways:
  * The default (`hue`) scales them down without changing their hue, so a bright orange torch stays orange.
  * `clip` cuts each channel off at white, exactly like Snipping Tool. To use it, open `Snapshot.exe.config` in Notepad, change `HdrToneMap` from `hue` to `clip`, and restart Snapshot.
* **HdrBrightness** (default `1.10`) makes HDR captures a little brighter than the v5 look. It only applies on HDR monitors, after converting by Windows' SDR white level and before the hue/clip step, so highlights still keep their colour instead of going flat white. Open `Snapshot.exe.config` in Notepad to change it, then restart Snapshot. `1.0` is exactly how v5 looked, `1.10` is the default (about 10% brighter), `1.2` is a bit brighter still. Values are limited to 0.5–2.0.
* Monitors in SDR mode still use the GDI path, unchanged.
* If anything in the HDR path fails (an old driver, a rotated or portrait HDR monitor, a protected window, etc.), that monitor falls back to the old capture, so nothing gets worse.

## v6
HDR captures are slightly brighter by default (`HdrBrightness=1.10` in `Snapshot.exe.config`). Set it to `1.0` to get the v5 look, or a little higher (for example `1.2`) if you want more. SDR monitors are unchanged.

## v5
v3 and v4 never actually used the HDR path. A COM call that fills in the monitor description failed on every monitor (`MarshalDirectiveException`: a pointer to a struct the runtime could not pass through as raw memory), so every capture fell back to the old GDI method. v5 fixes that struct (Windows wide strings are stored as 16-bit numbers, not C# `char`, which the runtime otherwise tries to recode) and passes every native struct as a raw pointer. The capture log now names the exact COM method if something throws, and records a one-line struct-size self-check.

**Diagnostics:** every capture appends a few lines to `%LOCALAPPDATA%\Snapshot\capture.log`. Tray menu → **Open capture log**. It shows:
* each monitor, with its coordinates, color space, and whether it's HDR
* the SDR white level
* whether HDR capture worked, and if not, why it fell back
* the brightest value in the frame
* how much of the screenshot came from the HDR path

Expect the HDR capture to look a little less punchy than what you see on the HDR monitor. That's normal: a PNG is an SDR image.
Exclusive-fullscreen games can block or confuse desktop capture. If that happens, switch the game to borderless/windowed fullscreen.

## Build (from Linux or Windows)
```
dotnet build -c Release -o out      # .NET SDK; reference assemblies come from NuGet
```
Output: `out/Snapshot.exe` (AnyCPU, runs 64-bit on x64). It only references Framework assemblies in the GAC (mscorlib, System, System.Core, System.Configuration, System.Drawing, System.Windows.Forms).
DPI: Per-Monitor V2 is requested in `app.manifest`, with a `SetProcessDpiAwarenessContext` fallback in `Program.Main`.

## Code map
* `Program.cs`: single-instance mutex and DPI fallback
* `GlobalUsings.cs`: shared using directives
* `TrayApp.cs`: tray icon and menu, hotkeys, capture flow, thumbnail stacking
* `HotkeyWindow.cs`: message-only window plus `RegisterHotKey`
* `RegionOverlay.cs`: frozen and dimmed selection overlay
* `ThumbnailForm.cs`: the non-activating floating preview, with hand-painted buttons and OLE file drag
* `EditorForm.cs`: the annotation editor window (hand-painted toolbar, zoom/pan, text typing, undo, copy/save)
* `Annotations.cs`: the annotation shapes (arrow, rectangle/ellipse, text) and how they draw
* `ScreenCapture.cs`: screen grab, PNG save, clipboard
* `CaptureLog.cs`: the diagnostic log
* `HdrCapture.cs`: HDR monitors: DXGI Desktop Duplication (FP16) plus tone mapping to sRGB, with SDR white level from DisplayConfig
* `Native.cs`: Win32 P/Invoke
