# Test Overlay Project Planning

## Purpose

This project is a Windows desktop utility for improving skill cooldown visibility in Mabinogi by duplicating selected quickslot cells into a user-positioned, click-through overlay.

The tool should not inspect game memory, inject code into the game process, hook the game's rendering pipeline, or automate gameplay input. The intended design is limited to official Windows screen/window capture APIs and a separate transparent overlay window.

## Core User Flow

1. The user selects the running game window.
2. The app captures the selected game window.
3. The app detects likely quickslot cells from the captured image.
4. The user reviews the detected candidates and manually checks, unchecks, adds, moves, or resizes cells.
5. The user arranges selected cells freely on an overlay layout.
6. In run mode, the app repeatedly crops the selected source cells from the current game window image and renders them at the configured overlay positions.
7. While running, the overlay must be click-through and must not consume game mouse input.
8. The user can pause or stop the overlay with a global hotkey or tray menu.

## Quickslot Model

Quickslot cells appear as mostly square icon slots, but their pixel size changes depending on game resolution, UI scale, Windows display scaling, and capture source. Therefore, slot size must not be a hard-coded global constant.

The project should use fixed-size crops only after calibration:

- Slot dimensions are profile-specific.
- The detector scans a broad slot-size range.
- The user can override width and height.
- Width and height are locked together by default, but advanced users may unlock them.
- Runtime capture uses the confirmed profile dimensions and does not continuously redetect slot size.

Suggested profile data:

```json
{
  "profileName": "mabinogi-3840x2160",
  "window": {
    "titleHint": "Mabinogi"
  },
  "captureResolution": {
    "width": 3840,
    "height": 2160
  },
  "slot": {
    "width": 42,
    "height": 42,
    "gapX": 2,
    "gapY": 2
  },
  "slots": [
    {
      "id": "slot-001",
      "enabled": true,
      "source": { "x": 0, "y": 0 },
      "overlay": { "x": 1200, "y": 300, "scale": 1.5, "opacity": 0.75 }
    }
  ]
}
```

## Detection Strategy

The detector should assist the user rather than attempt perfect automation.

Recommended pipeline:

1. Capture the selected game window client area.
2. Run multi-scale slot detection over a configurable range, initially around `20px` to `80px`.
3. Detect square-like candidates using edges, contrast changes, border patterns, and repeated spacing.
4. Prefer candidate groups that form rows, columns, or grids.
5. Give extra weight to regions near common quickslot locations such as top-left, top-center, left side, and right-side utility clusters, but do not rely on fixed absolute coordinates.
6. Present all candidates as editable overlays on the captured image.
7. Let the user finalize the exact source rectangles.

False positives are acceptable if they are easy to remove. Missing candidates are acceptable if manual add is fast.

## Calibration Editor Requirements

The editor is more important than perfect detection.

Required controls:

- Window selection.
- Capture refresh.
- Detected candidate overlay.
- Click to select or deselect candidates.
- Drag selection for multiple candidates.
- Manual add rectangle.
- Delete selected rectangles.
- Slot width and height controls.
- Group offset adjustment.
- Per-cell nudge controls.
- Snap to row, column, and equal spacing.
- Live crop preview for selected cells.
- Overlay layout preview.
- Save and load profiles.

## Run Mode Requirements

Run mode should:

- Capture only from the selected window or from the monitor area corresponding to the selected window if direct window capture is unavailable.
- Crop selected source rectangles using the saved profile.
- Render each crop into the configured overlay positions.
- Keep the overlay always on top.
- Keep the overlay transparent and click-through.
- Avoid consuming mouse clicks during normal operation.
- Support a global hotkey to pause, resume, or stop.
- Support a system tray menu.

## Technical Boundaries

Allowed design direction:

- Windows Graphics Capture.
- DXGI Desktop Duplication as fallback.
- Separate layered transparent overlay window.
- Global hotkeys.
- Local JSON profile storage.
- Image processing on captured frames.

Avoid:

- Game memory reads.
- DLL injection.
- DirectX/OpenGL render hooks into the game.
- Process-handle based inspection of protected game internals.
- Gameplay input automation.
- Anti-cheat bypass or concealment techniques.

## Technology Comparison

| Stack | UI Quality | Capture and Overlay Fit | Development Cost | Notes |
| --- | --- | --- | --- | --- |
| C# + WPF | Good | Good | Low to medium | Best MVP option. Mature Windows desktop tooling and easy Win32 interop. |
| C# + WinUI 3 | Very good | Good | Medium | Best final-product UI direction, but more setup friction than WPF. |
| C# + Avalonia | Good | Medium | Medium | Useful for cross-platform, but this project is Windows-specific. |
| C++ + Qt Widgets | Good | Very good | High | Strong native control, but polished UI takes more work. |
| C++ + Qt Quick/QML | Very good | Very good | High | Powerful, but too much complexity for first version. |
| C++ + ImGui | Tool-like | Very good | Medium | Fast for internal tools, less ideal for polished consumer UI. |
| Rust + Tauri + Web UI | Very good | Medium to good | High | Attractive UI and light runtime, but native capture/overlay integration adds complexity. |
| Electron + Web UI | Very good | Medium | Medium | Excellent UI, but heavier runtime and native module needs. |
| Python + PySide/PyQt | Good | Medium | Low | Good prototype option, weaker for polished distribution. |
| Java/Kotlin + JavaFX | Medium | Weak to medium | Medium | Windows native capture and overlay work is awkward. |
| Go + Wails/Fyne | Medium | Medium | Medium | Distribution is nice, UI polish and native interop are less compelling. |
| Dart + Flutter Desktop | Very good | Weak to medium | Medium | UI is strong, but Windows capture and overlay rely on native plugins. |

## Recommended Stack

MVP:

```text
C# + WPF
Windows Graphics Capture
Win32 layered click-through overlay
System.Text.Json profile storage
OpenCVSharp only if basic image processing is insufficient
```

Final product direction:

```text
C# + WinUI 3
Windows App SDK
Win32 interop for overlay behavior
Optional C++ native module only for proven capture or rendering bottlenecks
```

The primary reason to choose C# is that this product's hardest part is not raw performance. The hardest part is a clean calibration and layout editing experience. C# provides a strong balance between polished Windows UI and native API access.

## MVP Milestones

1. Create a desktop shell with profile storage.
2. Implement game window selection.
3. Capture a still image from the selected window.
4. Display the capture in a calibration editor.
5. Add manual fixed-size slot selection.
6. Add basic automatic candidate detection.
7. Save selected source slots.
8. Add overlay layout editing.
9. Implement click-through overlay rendering from still capture.
10. Replace still capture with live window capture.
11. Add global hotkey and tray controls.
12. Test with 1920x1080 and 4K scaled screenshots.

## Open Questions

- Which Mabinogi window modes must be supported: windowed, borderless fullscreen, exclusive fullscreen?
- Should the first public version require borderless/windowed mode?
- What should the default overlay layout be: horizontal row, vertical column, or freeform only?
- Should profiles be tied to window resolution, monitor resolution, or both?
- Should selected slots be grouped into named sets for different characters or skill pages?
