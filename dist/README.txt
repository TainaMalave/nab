nab - See it. Nab it.
A tiny screenshot tool for Windows 10 and 11.
https://github.com/TainaMalave/nab


GETTING STARTED
---------------
1. Keep nab.exe and nab.exe.config together in a permanent folder,
   for example Documents\nab.
2. Double-click nab.exe. A raccoon icon appears in the system tray
   (check the ^ arrow near the clock). No window opens.

   If Windows says "Windows protected your PC", click "More info",
   then "Run anyway". nab isn't code-signed yet, so Windows doesn't
   recognise it.

3. To quit, right-click the tray icon and choose Exit.


TAKING SCREENSHOTS
------------------
Ctrl+Shift+2    Capture a region (drag over what you want; Esc cancels)
Ctrl+Shift+1    Capture the whole monitor under the mouse

If another app already uses these, nab uses Ctrl+Alt+Shift+2 and
Ctrl+Alt+Shift+1 instead. Left-clicking the tray icon also captures
a region.

Every screenshot is copied to the clipboard and saved to
Pictures\nab. A thumbnail pops up in the bottom-right corner:
hover over it to Edit, Copy, Save As or open the Folder, or drag it
straight into Slack, Discord, an email or a browser.


EDITING
-------
Double-click the thumbnail (or click Edit) to open the editor.

V Select   A Arrow   R Rectangle   E Ellipse   H Highlighter
T Text     N Numbered steps        B Blur      C Crop

1 / 2 / 3       Size
F               Tool option (curved arrow, filled shape, text
                background, pixelate instead of blur)
Ctrl+Z / Ctrl+Y Undo / redo
Ctrl+C          Copy
Ctrl+S          Save over the original
Ctrl+Shift+S    Save as a new PNG or JPG
Ctrl+Enter      Done (save, copy and close)


TIPS
----
- Desktop shortcut: right-click nab.exe > Show more options >
  Send to > Desktop (create shortcut).
- Start with Windows: press Win+R, type shell:startup, and put a
  shortcut to nab.exe in the folder that opens.
- Settings: open nab.exe.config in Notepad, then restart nab.
- nab works offline. It never connects to the internet.


UNINSTALL
---------
Exit nab and delete its folder. To remove its settings and log too,
delete %LOCALAPPDATA%\nab. Your screenshots in Pictures\nab stay.


LICENSE
-------
Copyright (C) 2026 Taina Malave
nab is free software under the GNU General Public License v3 or
later. See LICENSE. The source code is at
https://github.com/TainaMalave/nab
