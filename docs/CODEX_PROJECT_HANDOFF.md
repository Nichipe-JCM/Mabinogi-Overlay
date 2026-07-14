# Codex Project Handoff

## Purpose

Mabinogi Overlay is a portable Windows desktop utility for mirroring selected Mabinogi quickslot cells into an always-on-top, click-through overlay. The current development branch also contains optional buff-duration and Tuairim gauge monitoring, plus an Erin time alarm tab.

The project must remain within this safety boundary:

- Use Windows capture APIs and a separate overlay window only.
- Do not read game memory, inject code, hook the game renderer, automate input, bypass anti-cheat, or conceal the application.

## Current Repository State

- Repository: `G:\gpt\git\testoverlayproj`
- Stable integration branch: `develop`
- Active feature branch at the time of this document: `feature/status-monitor-scaffold`
- Current app version in the project file: `0.0.3-beta`
- The active feature branch is ahead of `develop` and contains the status-monitor and Erin timer work. Do not assume that it has already been merged.
- Do not push unless the user explicitly requests it.

Before editing, always run:

```powershell
git status --short --branch
git log --oneline -10
```

Preserve any existing user changes. Use a purpose-specific branch for new work when the user requests one or when starting independent feature work.

## Verification Rules

The user performs runtime verification. Do not launch the application, operate its UI, capture the game window, run screenshot-based detection probes, or inspect the desktop unless the user explicitly asks.

Allowed routine verification:

```powershell
dotnet build src\TestOverlay.App\TestOverlay.App.csproj -o C:\Users\cjfal\Documents\Codex\build-check\MabinogiOverlay
```

Use a separate output directory because a locally running app can lock the default build output.

## Architecture

```text
App startup
  -> AppSettingsStore (settings.json)
  -> MainWindow
       -> ProfileStore (save/<profile>.json)
       -> capture / candidate / layout workflow
       -> status monitor workflow
       -> Erin timer tab
       -> OverlayWindow + InternalTimerOverlayWindow
```

### Main UI and orchestration

- `src/TestOverlay.App/MainWindow.xaml`
  - Main application layout, top capture commands, profile/overlay controls, and tabs.
- `src/TestOverlay.App/MainWindow.xaml.cs`
  - Primary orchestration layer. It currently owns capture, quickslot detection, profile state, overlay start/stop, monitor recognition state, sound alerts, and most UI events.
  - This file is intentionally the first place to inspect for cross-feature behavior, but it is large and should not grow indefinitely.
- `src/TestOverlay.App/LayoutEditorWindow.*`
  - Canvas editing, slot selection, multi-drag, snap, resize, and undo/redo.
- `src/TestOverlay.App/SettingsWindow.*`
  - Profile save folder, UI language, capture backend, automatic renderer selection, advanced renderer override, benchmark, log viewer, and settings reset.
- `src/TestOverlay.App/ErinTimerWindow.*`
  - In-app Erin time clock and persistent alarm list.

### Capture and rendering

- `WindowDiscoveryService` enumerates visible windows and prioritizes the exact `Client.exe` match.
- `WgcCaptureService` captures a selected WGC item. It is the required source for the GPU renderer.
- `DxgiDesktopDuplicationCaptureService` duplicates the selected window's monitor and crops its client area.
- `WindowCaptureService` is the GDI BitBlt fallback.
- `GpuLiveOverlayService` uses a persistent WGC session, D3D11, D2D, DXGI swap chain, and DirectComposition to draw quickslots on the GPU.
- `CpuCompositedOverlayRenderer` composites selected slots into a CPU bitmap.
- `OverlayWindow` is the quickslot surface. `InternalTimerOverlayWindow` is the monitor/timer/visual-alert surface. Both use Win32 transparent, no-activate, topmost behavior and return transparent hit tests.

Capture backend behavior is intentionally different:

- WGC targets the chosen game window. On supported Windows versions, the app requests borderless capture access and disables the capture border when allowed.
- DXGI and GDI capture desktop pixels for the selected client area; another foreground window can therefore affect their result.
- Automatic renderer selection maps WGC to GPU acceleration and DXGI/GDI to CPU compositing. GPU initialization failure also falls back to CPU compositing. Manual overrides remain under the advanced renderer section, and GPU acceleration requires WGC.

### Quickslot workflow

1. Capture the game window through auto or manual capture.
2. Drag an ROI with Auto detect section.
3. `RoiSectionDetectionService` routes wide ROIs to the top grouped pattern and tall ROIs to the vertical pattern.
4. The detector searches for square slot anchors, evaluates the full pattern, and adds a section plus editable candidates.
5. The user can correct candidates, create manual sections, and add selected candidates to the overlay.
6. The layout editor places slots on the overlay canvas.

Slot and gap calibration are profile-specific. Stored source coordinates are capture-pixel coordinates, not resolution-independent normalized coordinates.

### Status monitoring

The Buff/Tuairim tab is feature work on the current branch.

- `MonitorTemplateDetectionService` locates known buff icons and the Tuairim UI using bundled image templates.
- `MonitorValueRecognitionService` uses Windows OCR on source, light-mask, and dark-mask crops.
- Buff durations and Tuairim percent are reconciled by state guards in `MainWindow` instead of trusting one OCR result directly.
- Normal monitoring runs on a nominal two-second interval. Fast retries can occur while a value needs confirmation.
- Buff expiry needs five consecutive zero/inactive confirmations.
- Large downward buff-time changes need sustained confirmation.
- Tuairim accepts only 0-100, validates resets, rejects decreases other than a confirmed reset, and rejects implausibly large increases.
- Alert configuration, sound paths, volumes, enabled buff choices, and monitor anchors are saved in the active profile.

### Persistence and logs

- `settings.json` is stored next to the executable. It stores profile directory, automatic renderer selection, renderer override, capture backend, and language.
- Settings schema version 1 migrates legacy renderer/capture pairs: recommended pairs become automatic, while custom pairs remain manual overrides.
- Profiles accept the legacy `TuarimMonitorEnabled` spelling for upgrade compatibility and serialize only the corrected `TuairimMonitorEnabled` property.
- `save/<profile>.json` stores candidates, sections, layout, monitor settings, and monitor anchors.
- `Logs/app.log` contains the current session log.
- Missing or invalid settings fall back to defaults. Profile and settings files are directly rewritten; they are not yet atomically replaced.

## Recent Fixes

The most recent commits on the active branch repaired Erin alarm state persistence and added settings reset behavior.

- Erin alarm rows now use two-way bindings.
- Alarm normalization preserves object identity instead of recreating models during every save.
- Settings includes a two-step reset confirmation for the values in that dialog. Profiles and layouts are not deleted by this reset.

## Known Technical Risks

1. `MainWindow.xaml.cs` is a large orchestration file. New work should avoid adding more unrelated state directly there when a focused service/controller can own it.
2. DXGI currently creates capture resources for each captured frame. At high FPS this is materially more expensive than WGC's persistent frame session.
3. CPU render paths copy full frames into managed memory before cropping or compositing. This can be expensive at high resolution.
4. OCR runs sequentially for selected buff rows. The effective interval can exceed two seconds when OCR work is slow.
5. Monitor OCR and template matching require user runtime validation across UI scale, map brightness, and installed Windows OCR language packs.
6. Existing legacy handoff and patch-note files were replaced or updated to avoid stale instructions. Keep future documentation in UTF-8.

## Recommended Next Work Order

1. Obtain user runtime feedback for the current status monitor branch.
2. Stabilize capture performance before increasing default FPS or adding more continuously scanned UI.
3. Separate monitor orchestration and overlay session lifecycle from `MainWindow` when touching those areas again.
4. Add atomic profile/settings writes before broader distribution.
5. Merge the status-monitor branch into `develop` only after the user approves the runtime behavior.
