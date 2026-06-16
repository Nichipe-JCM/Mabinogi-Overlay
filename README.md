# Mabinogi Overlay

Mabinogi Overlay is a portable Windows utility that mirrors selected Mabinogi quickslot cells into a small always-on-top overlay.

It is designed for players who want clearer cooldown visibility without modifying the game client. The app does not read game memory, inject code, hook the renderer, or automate input.

## Disclaimer

Mabinogi Overlay is an unofficial utility and is not affiliated with, endorsed by, or supported by Nexon. Use it at your own discretion and follow the rules that apply to your game service region.

Current version: `0.0.2-beta`

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

## Technology Stack

- **Language:** C#
- **Runtime:** .NET 8
- **UI:** WPF with WPF-UI
- **Platform:** Windows
- **Capture:** Windows Graphics Capture, DXGI Desktop Duplication, GDI BitBlt
- **Graphics interop:** Direct3D 11, DXGI, Direct2D, DirectComposition
- **Native integration:** Win32 window styles, global hotkey registration, click-through overlay behavior
- **Storage:** JSON profiles and settings via `System.Text.Json`

## License

Mabinogi Overlay is licensed under the [MIT License](LICENSE).

Third-party dependencies remain under their own licenses. The current primary NuGet dependencies, WPF-UI and Vortice packages, are also MIT-licensed.

## Notes

This app is a beta version. Some features may still contain bugs. Please report bugs through GitHub Issues.

WGC capture may show the Windows capture border depending on system behavior. DXGI and GDI are available for comparison, but WGC is still the most reliable option for capturing the selected game window during setup.

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

## Korean

Korean documentation is available at [docs/README.ko.md](docs/README.ko.md).
