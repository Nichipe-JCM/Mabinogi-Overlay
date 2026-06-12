# Mabinogi Overlay

Mabinogi Overlay is a Windows desktop utility for duplicating selected Mabinogi quickslot cells into a configurable, click-through overlay. It is intended to improve cooldown visibility without reading game memory, injecting code, hooking the renderer, or automating input.

Current version: `0.0.1`

## Project Status

This is an early portable Windows build. The core workflow is implemented, but detection accuracy and layout ergonomics are still being tuned against real Mabinogi UI layouts, resolutions, DPI settings, and quickslot configurations.

## Core Features

- Select a running Mabinogi `Client.exe` window.
- Verify and use Windows Graphics Capture.
- Capture the game window into an editable preview.
- Auto-detect quickslot sections from a user-drawn ROI.
- Support top grouped quickslot sections and vertical quickslot sections.
- Review, multi-select, move, resize, add, and delete slot candidates.
- Add selected candidates to an overlay layout without replacing the existing layout.
- Edit overlay placement, canvas size, per-slot opacity, per-slot scale, grid snap, max FPS, and stop hotkey.
- Preview overlay placement on the real screen before running.
- Run a topmost click-through overlay that should not consume game mouse input.
- Save and load portable JSON profiles, including all slot candidates, sections, overlay slots, and layout settings.

## Technology Stack

### Runtime and UI

- **Language:** C#
- **Runtime:** .NET 8
- **Target framework:** `net8.0-windows10.0.19041.0`
- **UI framework:** WPF
- **Platform:** Windows only

WPF was chosen because the first version needs a practical Windows desktop calibration tool more than a cross-platform UI. The app depends on Windows capture and overlay behavior, so a Windows-native stack keeps the implementation direct.

### Capture

- **Primary capture API:** Windows Graphics Capture
- **Interop:** WinRT `Windows.Graphics.Capture`
- **Graphics bridge:** Direct3D 11 interop
- **Frame handling:** persistent live capture session with `Direct3D11CaptureFramePool`
- **Static capture:** one-shot WGC capture for preview and calibration

The live overlay path keeps a WGC capture session open and processes arriving frames, instead of repeatedly creating and destroying capture sessions. This is more performance-friendly and reduces capture flicker and overhead.

### Overlay Window

- **Windowing:** WPF overlay window
- **Native behavior:** Win32 extended window styles
- **Click-through flags:** `WS_EX_TRANSPARENT`, `WS_EX_LAYERED`, `WS_EX_NOACTIVATE`, `WS_EX_TOOLWINDOW`
- **Topmost behavior:** Win32 topmost positioning
- **Hotkey:** Win32 global hotkey registration

The overlay is designed to be visible above the game while allowing mouse input to pass through to the game window.

### Detection and Calibration

- **Detection style:** ROI-based quickslot section detection
- **Anchor strategy:** bottom-right border anchored scanning
- **Pattern scoring:** full-section scoring after candidate anchor generation
- **Supported patterns:**
  - `Top grouped 4x2 x3`
  - `Vertical 2x8`
- **Manual correction:** candidate drag, box selection, Ctrl/Shift multi-select, keyboard nudge, undo/redo

Detection is treated as an assistant, not as an absolute source of truth. The user can correct all detected slot rectangles before adding them to the overlay.

### Profile Storage

- **Format:** JSON
- **Serializer:** `System.Text.Json`
- **Default portable path:** `save` folder next to the executable
- **Settings file:** `settings.json` next to the executable

Profiles store:

- all slot candidates
- quickslot sections
- overlay slots
- canvas size
- screen position
- opacity
- slot scale
- max FPS
- stop hotkey
- grid snap size
- manual section settings

## Repository Layout

```text
src/
  TestOverlay.App/
    MainWindow.xaml(.cs)                  Main calibration and control UI
    LayoutEditorWindow.xaml(.cs)          Overlay layout editor
    OverlayWindow.xaml(.cs)               Runtime click-through overlay
    OverlayPlacementPreviewWindow.xaml(.cs)
    Models/                               App state and profile models
    Native/                               Win32 and Direct3D interop
    Services/                             Capture, detection, profile, hotkey services
tools/
  TestOverlay.DetectionProbe/             Detection test/probe utility
docs/
  PLANNING.md
  MVP_MANUAL_TEST.md
  NEXT_SESSION_HANDOFF.md
```

## Build

Requirements:

- Windows 10 2004 or later
- .NET 8 SDK

Build the app:

```powershell
dotnet build G:\gpt\git\testoverlayproj\src\TestOverlay.App\TestOverlay.App.csproj
```

## Portable Publish

Recommended publish command:

```powershell
dotnet publish G:\gpt\git\testoverlayproj\src\TestOverlay.App\TestOverlay.App.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  /p:PublishSingleFile=true `
  /p:PublishReadyToRun=true `
  /p:PublishTrimmed=false `
  -o G:\gpt\git\testoverlayproj\artifacts\MabinogiOverlay-0.0.1
```

Recommended package layout:

```text
MabinogiOverlay-0.0.1/
  Mabinogi Overlay.exe
  README.md
  save/
```

Run the app from a writable folder. Avoid `Program Files` for portable builds unless you are prepared to handle restricted write permissions.

## Manual Test Checklist

Before packaging a public build:

- Confirm `Client.exe` is prioritized in the window list.
- Run `Verify WGC`.
- Capture the game window and confirm the preview image is correct.
- Auto-detect each required quickslot section.
- Add selected slots to the overlay.
- Open Layout Editor and arrange the overlay.
- Use Screen Preview to align the overlay with the game screen.
- Start the overlay.
- Confirm the overlay stays visible above the game.
- Confirm the overlay does not steal focus or consume mouse clicks.
- Confirm Stop button and stop hotkey work.
- Save and reload a profile from the portable `save` folder.

## Safety Boundary

This project intentionally avoids:

- game memory reads
- process injection
- DLL injection
- DirectX/OpenGL render hooks into the game
- gameplay input automation
- anti-cheat bypass behavior

It uses official Windows capture APIs and a separate transparent overlay window.

## Korean Documentation

Korean documentation is available at [docs/README.ko.md](docs/README.ko.md).
