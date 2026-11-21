# MediaDesk branding

Approved direction B: Directory Search. The blue folder, image frame and magnifier represent finding photos and videos within a chosen directory.

- `mediadesk.svg`: editable master, 1024 × 1024 view box.
- `mediadesk-1024.png`: transparent desktop artwork.
- `mediadesk.ico`: Windows icon with 16, 24, 32, 48, 64, 128 and 256 pixel images.
- Electron uses a 256 pixel copy at `apps/desktop/src/main/app-icon.png` and a 32 pixel copy at `apps/desktop/src/main/tray.png`.
- The renderer favicon uses `apps/desktop/src/renderer/public/mediadesk.svg`.
- Electron Builder uses this ICO for the application and NSIS installer/uninstaller. The WebView host embeds the same ICO for its executable, windows and tray.

Keep the existing application ID, data directory and startup registry key when changing display branding, so existing libraries and settings remain available.
