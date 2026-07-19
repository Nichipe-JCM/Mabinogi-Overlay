# Mabinogi Overlay

Mabinogi Overlay is a portable Windows utility that mirrors selected Mabinogi quickslot cells into a small always-on-top overlay.

It is designed for players who want clearer cooldown visibility without modifying the game client. The app does not read game memory, inject code, hook the renderer, or automate input.

## Korean

Korean documentation is available at [docs/README.ko.md](docs/README.ko.md).

Development handoff and architecture notes are available at [docs/CODEX_PROJECT_HANDOFF.md](docs/CODEX_PROJECT_HANDOFF.md).

## Disclaimer

Mabinogi Overlay is an unofficial utility and is not affiliated with, endorsed by, or supported by Nexon. Use it at your own discretion and follow the rules that apply to your game service region.

Current version: `0.0.4.3`

This program was developed with assistance from OpenAI Codex and ChatGPT.

## What It Does

- Captures the Mabinogi client window.
- Detects quickslot sections from a selected screen area.
- Lets you correct detected slots manually when needed.
- Adds selected slots to a separate overlay layout.
- Shows the overlay above the game while mouse clicks pass through to the game.
- Saves layouts and candidates as portable profiles.
- Supports English and Korean UI.

## Basic Workflow

1. Start Mabinogi and open the quickslots you want to mirror.
2. Run Mabinogi Overlay.
3. Use Auto capture or Manual capture to load the game image into the preview.
4. Use Auto detect section and drag over a quickslot section.
5. Select the slots you want and add them to the overlay.
6. Open Manage Layout to position, scale, and arrange the overlay.
7. Start the overlay.

## Main Features

- Auto capture for the detected Mabinogi `Client.exe` window.
- Manual WGC capture when explicit window selection is needed.
- Capture backend options: WGC, DXGI, and GDI.
- Renderer options: GPU/DXGI, improved CPU/composited, and existing CPU/WPF.
- ROI-based quickslot section detection.
- Automatic horizontal/vertical section routing based on ROI shape.
- Manual candidate creation, movement, resize, deletion, and multi-selection.
- Per-slot scale and opacity overrides.
- Global opacity, global slot scale, grid snap, max FPS, and stop hotkey settings.
- Screen preview window for positioning the overlay on the real monitor.
- Session log viewer for troubleshooting.
- Portable profile storage with selectable save folder.
- Profile creation, explicit loading, and automatic saving after layout changes.
- Dedicated profile and overlay control sections in the main window.
- Optional buff-duration monitoring for Battlefield, March, Vivace, and Song of rich year.
- Optional Tuairim gauge monitoring with threshold-based sound and visual alerts.
- An integrated Erin time tab with persistent alarms.

## Technology Stack

- **Language:** C#
- **Runtime:** .NET 8
- **UI:** WPF with WPF-UI
- **Platform:** Windows
- **Capture:** Windows Graphics Capture, DXGI Desktop Duplication, GDI BitBlt
- **Graphics interop:** Direct3D 11, DXGI, Direct2D, DirectComposition
- **Native integration:** Win32 window styles, global hotkey registration, click-through overlay behavior
- **Storage:** JSON profiles and settings via `System.Text.Json`
- **Text recognition:** Windows OCR with image-mask fallbacks for monitored values

## Build and Test

```powershell
dotnet build MabinogiOverlay.sln -c Release
dotnet run --project tests/TestOverlay.App.Tests/TestOverlay.App.Tests.csproj -c Release
```

The automated tests cover renderer/capture compatibility policy, invalid profile rejection, atomic backup recovery, and backup-only profile discovery.

## License

Mabinogi Overlay is licensed under the [MIT License](LICENSE).

Third-party dependencies remain under their own licenses. The current primary NuGet dependencies, WPF-UI and Vortice packages, are also MIT-licensed.

## Notes

This app is a beta version. Some features may still contain bugs. Please report bugs through GitHub Issues.

The buff/Tuairim monitor and Erin timer are active development features on the current branch. Their accuracy depends on game UI scale, capture backend, map brightness, and installed Windows OCR language support.

WGC is the default capture backend and the input required by the GPU/DXGI renderer. On supported Windows versions, the app requests borderless capture access and hides the Windows capture border when allowed. DXGI monitor capture uses the improved CPU/composited renderer, while GDI remains a compatibility fallback.

## Safety Boundary

Mabinogi Overlay intentionally avoids:

- game memory reads
- process injection
- DLL injection
- game renderer hooks
- gameplay input automation
- anti-cheat bypass or hiding behavior
- packet sniffing or traffic interception

It uses Windows capture APIs and a separate transparent overlay window.

 
