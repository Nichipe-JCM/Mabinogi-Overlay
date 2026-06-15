# Mabinogi Overlay

Mabinogi Overlay is a portable Windows desktop utility for duplicating selected Mabinogi quickslot cells into a configurable, click-through overlay. It is intended to improve cooldown visibility without reading game memory, injecting code, hooking the game renderer, or automating input.

Current version: `0.0.2-beta`

## Status

This is an early beta build. The main capture, detection, layout, profile, localization, and overlay workflows are implemented, but runtime behavior should still be validated against real Mabinogi clients, resolutions, DPI settings, quickslot layouts, and capture backends before each public release.

## Highlights Since 0.0.1

- Added selectable capture backends: WGC window capture, DXGI desktop duplication, and GDI BitBlt.
- Split preview capture into Auto capture and Manual capture flows.
- Added GPU/DXGI live overlay rendering and an improved CPU composited renderer.
- Added renderer benchmark tooling with configurable 30-second stress tests and GPU cases.
- Improved WGC live capture handling and runtime diagnostics.
- Blocked Layout Editor while the live overlay is running.
- Improved dark-map ROI detection, one-pixel border handling, oversized slot normalization, and dark empty-area rejection.
- Added session-level detect diagnostics.
- Redesigned the management UI with WPF UI styling, custom title bar, app logo/icon, waiting capture state, and cleaner settings layout.
- Added Korean localization and immediate language refresh support.
- Moved profile, renderer, capture backend, language, log, and benchmark controls into Settings.
- Improved candidate panel behavior, manual section popup behavior, selection styling, and Ctrl/Shift multi-select.
- Auto-routes ROI section detection by selection shape: wide ROI uses top grouped detection, tall ROI uses vertical detection.
- Added right-click cancellation while dragging/selecting in capture preview.
- Unified detection active state and drag selection colors with the project accent color `#89DED4`.
- Fixed global slot scale so values below `0.5x` affect actual overlay slot size.

## Core Features

- Prioritize and select a running Mabinogi `Client.exe` window.
- Capture the game window into an editable preview.
- Use Auto capture for the detected Mabinogi client, or Manual capture for explicit WGC selection.
- Auto-detect quickslot sections from a user-drawn ROI.
- Support top grouped quickslot sections and vertical quickslot sections.
- Review, multi-select, move, resize, add, and delete slot candidates.
- Add selected candidates to the overlay layout without replacing existing overlay slots.
- Use manual candidate and manual section tools when detection needs correction.
- Edit overlay canvas size, screen position, global opacity, global slot scale, grid snap, max FPS, stop hotkey, per-slot opacity, and per-slot scale.
- Preview overlay placement on the real screen before starting the overlay.
- Run a topmost click-through overlay that should not consume mouse input.
- Save and load portable JSON profiles, including all slot candidates, sections, overlay slots, and layout settings.
- Switch UI language between English and Korean.
- Open session logs from Settings for runtime and detection diagnostics.

## Technology Stack

### Runtime and UI

- **Language:** C#
- **Runtime:** .NET 8
- **Target framework:** `net8.0-windows10.0.19041.0`
- **UI framework:** WPF
- **UI library:** WPF-UI
- **Platform:** Windows only

The app is Windows-specific because capture, overlay, click-through behavior, global hotkeys, and DirectX interop all depend on Windows APIs. WPF keeps the calibration and management UI close to the platform behavior being tested.

### Capture

- **WGC window capture:** primary option for accurate Mabinogi window preview capture.
- **DXGI desktop duplication:** monitor capture option that avoids the WGC yellow border, useful for live overlay comparisons.
- **GDI BitBlt:** fallback client-area capture option.
- **Interop:** WinRT `Windows.Graphics.Capture`, Direct3D 11, DXGI, Direct2D, DirectComposition.

Preview capture still benefits from WGC because it can capture the selected Mabinogi window directly. Live overlay can use alternate capture backends when their behavior is acceptable for the user's setup.

### Overlay Rendering

Supported renderer modes:

- **Existing CPU/WPF:** original WPF-based overlay update path.
- **Improved CPU/Composited:** CPU crop/update path optimized for lower GPU dependency.
- **GPU/DXGI:** GPU-oriented live overlay path with DXGI/DirectComposition support and CPU fallback when the selected capture path cannot support it.

The default renderer setting is GPU/DXGI. Runtime logs record renderer fallback and performance diagnostics.

### Overlay Window

- **Windowing:** WPF overlay window plus native Win32 styles.
- **Click-through flags:** `WS_EX_TRANSPARENT`, `WS_EX_LAYERED`, `WS_EX_NOACTIVATE`, `WS_EX_TOOLWINDOW`.
- **Topmost behavior:** Win32 topmost positioning.
- **Stop hotkey:** Win32 global hotkey registration.

The overlay is designed to remain visible above the game while allowing mouse input to pass through to the game window.

### Detection and Calibration

- **Detection style:** ROI-based quickslot section detection.
- **Routing:** ROI aspect ratio chooses the section pattern automatically.
- **Anchor strategy:** bottom-right border oriented scanning.
- **Scoring:** generate anchor candidates first, then score full section patterns.
- **Supported patterns:**
  - `Top grouped 4x2 x3`
  - `Vertical 2x8`
- **Dark map handling:** one-pixel border preference, oversized fit normalization, adaptive inset content checks, and dark empty-area rejection.
- **Manual correction:** candidate drag, box selection, Ctrl/Shift multi-select, keyboard nudge, grid snap, undo/redo.

Detection is treated as an assistant, not as an absolute source of truth. The user can correct all detected slot rectangles before adding them to the overlay.

## Profile and Settings Storage

- **Profile format:** JSON via `System.Text.Json`
- **Portable default profile path:** `save` folder next to the executable
- **Settings file:** `settings.json` next to the executable
- **Configurable profile directory:** available in Settings

Profiles store:

- all slot candidates
- quickslot sections
- overlay slots
- canvas size
- screen position
- global opacity
- global slot scale
- per-slot opacity overrides
- per-slot scale
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
    BenchmarkWindow.xaml(.cs)             Renderer benchmark UI
    LogWindow.xaml(.cs)                   Session log viewer
    Models/                               App state and profile models
    Native/                               Win32, WinRT, Direct3D, DXGI interop
    Services/                             Capture, detection, profile, settings, logging services
tools/
  TestOverlay.DetectionProbe/             Detection test/probe utility
docs/
  README.ko.md                            Korean README
  PLANNING.md                             Local planning notes
  MVP_MANUAL_TEST.md                      Manual test notes
```

## Build

Requirements:

- Windows 10 2004 or later
- .NET 8 SDK

Build the app:

```powershell
dotnet build G:\gpt\git\testoverlayproj\src\TestOverlay.App\TestOverlay.App.csproj
```

Build the detection probe:

```powershell
dotnet build G:\gpt\git\testoverlayproj\tools\TestOverlay.DetectionProbe\TestOverlay.DetectionProbe.csproj
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
  -o G:\gpt\git\testoverlayproj\artifacts\MabinogiOverlay-0.0.2-beta
```

Recommended package layout:

```text
MabinogiOverlay-0.0.2-beta/
  Mabinogi Overlay.exe
  README.md
  save/
```

Run portable builds from a writable folder. Avoid `Program Files` unless write permissions for `settings.json` and the `save` folder are handled.

## Manual Smoke Test

Before packaging a public build:

- Confirm Auto capture finds the Mabinogi `Client.exe` window.
- Confirm Manual capture can capture the selected Mabinogi window with WGC.
- Confirm Settings can switch capture backend and renderer mode.
- Capture a bright map and a dark map, then run Auto detect section.
- Confirm wide ROI routes to top grouped detection and tall ROI routes to vertical detection.
- Confirm right-click cancels active capture-preview drag/detect selection.
- Add selected slots to the overlay without replacing existing slots.
- Open Layout Editor and verify global slot scale, per-slot scale, opacity, grid snap, and undo/redo.
- Open Screen Preview and align the overlay to the game screen.
- Start the overlay and confirm it remains visible above the game.
- Confirm the overlay does not steal focus or consume mouse clicks.
- Confirm Overlay stop and the stop hotkey work.
- Save and reload a profile from the portable `save` folder.
- Switch language and confirm visible labels refresh.
- Open Settings > Log and confirm session logs are available.

## Safety Boundary

This project intentionally avoids:

- game memory reads
- process injection
- DLL injection
- DirectX/OpenGL hooks into the game process
- gameplay input automation
- anti-cheat bypass or hiding behavior

It uses official Windows capture APIs and a separate transparent overlay window.

## Korean Documentation

Korean documentation is available at [docs/README.ko.md](docs/README.ko.md).
