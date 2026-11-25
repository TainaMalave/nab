# nab 0.9.1

(Formerly called Snapshot.)

A tiny CleanShot-style screenshot tool for Windows 10/11. It's one small `nab.exe` (about 400 KB) built for .NET Framework 4.8, which ships with Windows 10 (1903+) and 11, so there's nothing to install. `nab.exe.config` is optional: the exe runs without it.

## Run
1. Double-click `nab.exe`. If SmartScreen shows "Windows protected your PC" (the exe is unsigned), click **More info**, then **Run anyway**.
2. A purple raccoon icon appears in the system tray (it may be under the `^` overflow arrow). No window opens.
3. To quit, right-click the tray icon and choose **Exit**. To remove it, delete the exe. It writes no registry entries; besides your screenshots in `Pictures\nab`, it keeps a small log and the editor's last-used settings (`capture.log`, `editor.ini`) in `%LOCALAPPDATA%\nab`. Images dragged out of the editor are temporary copies in `%TEMP%\nab`, cleared after a day.
4. Upgrading from Snapshot: exit the old Snapshot from its tray icon first (nab tells you if it's still running). Screenshots taken before the rename stay in `Pictures\Snapshots`; the editor picks up your old settings automatically.

## Hotkeys
| Action | Hotkey | Fallback if another app owns it |
|---|---|---|
| Capture region | **Ctrl+Shift+2** | Ctrl+Alt+Shift+2 |
| Capture full screen (monitor under the mouse) | **Ctrl+Shift+1** | Ctrl+Alt+Shift+1 |

The startup notification and the tray menu show which hotkeys were actually registered.
Left-clicking the tray icon also starts a region capture.
Note: while nab runs, these hotkeys are global, so Excel's Ctrl+Shift+1 and Ctrl+Shift+2 number-format shortcuts won't reach Excel.

## Region capture
The screen freezes and dims across all monitors. Drag a rectangle (the live size shows in pixels).
**Esc** or **right-click** cancels.

## After every capture
* The image is copied to the clipboard (as a bitmap and as PNG).
* A PNG is saved to `Pictures\nab\nab YYYY-MM-DD at HH.MM.SS.png`.
* A **floating thumbnail** appears at the bottom-right. It never takes focus, so you can keep typing.
  * It disappears after about 6 seconds. Hovering keeps it open.
  * On hover you get **Edit**, **Copy**, **Save As** (PNG or JPG), **Folder** (opens Explorer with the file selected), and **✕**.
  * **Double-click** the thumbnail (or click **Edit**) to open the annotation editor.
  * **Drag the thumbnail** into Slack, Discord, an email, a browser upload box, or an Explorer folder to drop the PNG file there.
  * New captures stack upward. If the stack gets too tall, the oldest is removed.

## Annotation editor (v7, more tools in v8, editing and drag-out in v9)
Opens on the monitor the screenshot came from, at 100% if it fits, otherwise zoomed to fit.

| Tool | Key | Notes |
|---|---|---|
| Select | **V** | Click a shape to select it, then drag to move it. Rectangles, ellipses, blurs and highlighter strokes get 8 resize handles. Arrows get a handle at each end plus one in the middle: drag the middle to bend the arrow (back to the line makes it straight). **Arrow keys** nudge (Shift = 10 px), **Delete** removes, **double-click** text to edit it, **Esc** deselects. Changing colour, size or the F toggle restyles the selected shape. |
| Arrow | **A** | Drag. Hold **Shift** to snap to 45°. Press **F** for **curved** arrows: the curve follows the arc you drag (a straight drag gets a gentle bend). |
| Rectangle | **R** | Drag. Hold **Shift** for a square. **F** = filled. |
| Ellipse | **E** | Drag. Hold **Shift** for a circle. **F** = filled. |
| Highlighter | **H** | See-through marker. Drag freehand, or hold **Shift** for a straight line. |
| Text | **T** | Click and type. **Enter** = new line, **Esc** or click elsewhere = finish. Click existing text to edit it. **F** = coloured background. |
| Numbered steps | **N** | Click to drop 1, 2, 3… (hold and drag to position). Numbers re-flow if you undo one. |
| Blur | **B** | Drag over private details. **F** switches to **pixelate**. Baked into the saved file, so it can't be undone by whoever receives it. |
| Crop | **C** | Shows the whole picture with the current crop. Drag the area to keep. **Esc**, **Enter** or **C** leaves crop mode; **Ctrl+Z** undoes a crop. |

* **Size:** three choices (keys **1**, **2**, **3**). Sets line thickness, font size, marker width, step size, or blur strength.
* **F toggle:** the button next to the sizes; what it does depends on the tool (see the table).
* **Colours:** red, yellow, green, blue, black, white, plus a custom colour picker. Changing colour, size, or fill while typing restyles that text.
* The editor remembers your last tool, colours, size, and toggles, even after nab restarts (saved in `%LOCALAPPDATA%\nab\editor.ini`).
* **Undo / redo:** Ctrl+Z, Ctrl+Y (or Ctrl+Shift+Z).
* **Zoom:** Ctrl+mouse wheel, Ctrl+plus/minus, **Ctrl+1** = 100%, **Ctrl+0** = fit. Click the % in the toolbar to switch between them. Scroll with the wheel (Shift+wheel sideways); pan with the middle mouse button or **Space**+drag.
* **Drag** (the toolbar button with the dots): drag it onto Slack, Discord, an email, a browser upload box or a folder to drop the annotated image as a PNG, like the thumbnail. If nothing changed since the last save it drops the original file; otherwise a copy with the same name.
* **Copy** (Ctrl+C) copies the annotated image.
* **Save** (Ctrl+S) saves over the original PNG in `Pictures\nab`.
* **Save As** (Ctrl+Shift+S) writes a new PNG or JPG and leaves the original alone.
* **Done** (Ctrl+Enter) saves over the original if anything changed, copies, and closes.
* Closing with unsaved changes asks first. Exiting nab from the tray also asks for each open editor.
* On a narrow window (like a portrait monitor) the toolbar wraps onto extra rows.

To open the editor automatically after every capture (instead of showing the thumbnail), open `nab.exe.config` in Notepad, set `OpenEditorAfterCapture` to `true`, and restart nab. It's off by default.

## HDR displays (new in v3)
With Windows HDR turned on, the classic GDI screen grab returns the desktop without tone mapping, so games (World of Warcraft, etc.) came out blown out and washed out.
v3 detects which monitors are in HDR mode and captures those through DXGI Desktop Duplication in 16-bit floating-point (scRGB). It then tone-maps the image to normal 8-bit sRGB:
* Windows' **SDR content brightness** setting maps to white, the same conversion Snipping Tool uses. Everything at or below that brightness is left exactly as-is, so contrast and saturation match Snipping Tool.
* v4 handles colors brighter than SDR white two ways:
  * The default (`hue`) scales them down without changing their hue, so a bright orange torch stays orange.
  * `clip` cuts each channel off at white, exactly like Snipping Tool. To use it, open `nab.exe.config` in Notepad, change `HdrToneMap` from `hue` to `clip`, and restart nab.
* **HdrBrightness** (default `1.10`) makes HDR captures a little brighter than the v5 look. It only applies on HDR monitors, after converting by Windows' SDR white level and before the hue/clip step, so highlights still keep their colour instead of going flat white. Open `nab.exe.config` in Notepad to change it, then restart nab. `1.0` is exactly how v5 looked, `1.10` is the default (about 10% brighter), `1.2` is a bit brighter still. Values are limited to 0.5–2.0.
* Monitors in SDR mode still use the GDI path, unchanged.
* If anything in the HDR path fails (an old driver, a rotated or portrait HDR monitor, a protected window, etc.), that monitor falls back to the old capture, so nothing gets worse.

## v6
HDR captures are slightly brighter by default (`HdrBrightness=1.10` in `nab.exe.config`). Set it to `1.0` to get the v5 look, or a little higher (for example `1.2`) if you want more. SDR monitors are unchanged.

## v5
v3 and v4 never actually used the HDR path. A COM call that fills in the monitor description failed on every monitor (`MarshalDirectiveException`: a pointer to a struct the runtime could not pass through as raw memory), so every capture fell back to the old GDI method. v5 fixes that struct (Windows wide strings are stored as 16-bit numbers, not C# `char`, which the runtime otherwise tries to recode) and passes every native struct as a raw pointer. The capture log now names the exact COM method if something throws, and records a one-line struct-size self-check.

**Diagnostics:** every capture appends a few lines to `%LOCALAPPDATA%\nab\capture.log`. Tray menu → **Open capture log**. It shows:
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
Output: `out/nab.exe` (AnyCPU, runs 64-bit on x64). It only references Framework assemblies in the GAC (mscorlib, System, System.Core, System.Configuration, System.Drawing, System.Windows.Forms).
DPI: Per-Monitor V2 is requested in `app.manifest`, with a `SetProcessDpiAwarenessContext` fallback in `Program.Main`.

## Code map
* `Program.cs`: single-instance mutex and DPI fallback
* `GlobalUsings.cs`: shared using directives
* `TrayApp.cs`: tray icon and menu, hotkeys, capture flow, thumbnail stacking
* `HotkeyWindow.cs`: message-only window plus `RegisterHotKey`
* `RegionOverlay.cs`: frozen and dimmed selection overlay
* `ThumbnailForm.cs`: the non-activating floating preview, with hand-painted buttons and OLE file drag
* `EditorForm.cs`: the annotation editor window (hand-painted toolbar, zoom/pan, text typing, undo, copy/save)
* `Annotations.cs`: the annotation shapes (arrow, rectangle/ellipse, highlighter, numbered step, blur/pixelate, text) and how they draw
* `EditorSettings.cs`: remembers the editor's last tool, colours, size and toggles in `editor.ini`
* `ScreenCapture.cs`: screen grab, PNG save, clipboard
* `CaptureLog.cs`: the diagnostic log
* `HdrCapture.cs`: HDR monitors: DXGI Desktop Duplication (FP16) plus tone mapping to sRGB, with SDR white level from DisplayConfig
* `Native.cs`: Win32 P/Invoke

## License
Copyright (C) 2026 Taina Malave

nab is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, version 3 of the License or (at your option) any later version.

nab is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the [GNU General Public License](LICENSE) for more details.
