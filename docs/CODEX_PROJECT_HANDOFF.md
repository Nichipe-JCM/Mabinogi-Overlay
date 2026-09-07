# Codex Project Handoff

## Purpose

Mabinogi Overlay is a portable Windows desktop utility for mirroring selected Mabinogi quickslot cells into an always-on-top, click-through overlay. The current development branch also contains optional buff-duration and Tuairim gauge monitoring, plus an Erin time alarm tab.

The project must remain within this safety boundary:

- Use Windows capture APIs and a separate overlay window only.
- Do not read game memory, inject code, hook the game renderer, automate input, bypass anti-cheat, or conceal the application.

## Current Repository State

- Repository: `G:\gpt\git\testoverlayproj`
- Stable integration branch: `develop`
- Active stabilization branch: `codex/msr-stabilization`
- Next planned release branch: `version/0.0.5-beta`
- Current app version in the project file: `0.0.5-beta`
- Profile management, the in-app guide, and the WGC one-shot capture thread fix are merged into `develop`.
- Follow the repository-root `AGENTS.md` for working rules. Commit verified changes by feature and push the working branch by default unless the user requests a hold.

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
  -> AppSettingsStore (%LocalAppData%/Mabinogi Overlay/settings.json)
  -> MainWindow
       -> ProfileStore (%LocalAppData%/Mabinogi Overlay/Profiles/<profile>.json)
       -> capture / candidate / layout workflow
       -> status monitor workflow
       -> Erin timer tab
       -> OverlayWindow + InternalTimerOverlayWindow
```

### Main UI and orchestration

- `CompactControlWindow` is the small operational shell for already-configured profiles. It delegates overlay and monitor actions back to `MainWindow` and does not own a second copy of runtime state.
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
- GPU rendering uses its own GPU capture session. When monitor OCR also needs CPU-readable WGC frames, the secondary conversion path is capped at 2 FPS instead of copying every source frame.
- Buff anchor evaluation copies only the union of saved anchor bounds, and OCR text masks are generated lazily after the raw OCR attempt fails.
- Buff anchors are classified as active, inactive, or indeterminate. Indeterminate samples preserve validation progress, while a separate retry policy backs persistent verification off after the initial retry burst.
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
- Buff durations and Tuairim percent are reconciled by `StatusObservationController` instead of trusting one OCR result directly.
- Normal monitoring runs on a nominal two-second interval. Fast retries can occur while a value needs confirmation.
- Buff expiration and timer corrections use buff-specific rules in `StatusObservationController`; music and status buffs do not share a single confirmation count.
- Large downward buff-time changes need sustained confirmation.
- Tuairim accepts only 0-100, validates resets, rejects decreases other than a confirmed reset, and rejects implausibly large increases.
- Alert configuration, sound paths, volumes, enabled buff choices, and monitor anchors are saved in the active profile.

### Persistence and logs

- `settings.json` is stored under `%LocalAppData%\Mabinogi Overlay`. It stores profile directory, automatic renderer selection, renderer override, capture backend, and language.
- Settings schema version 1 migrates legacy renderer/capture pairs: recommended pairs become automatic, while custom pairs remain manual overrides.
- Profiles accept the legacy `TuarimMonitorEnabled` spelling for upgrade compatibility and serialize only the corrected `TuairimMonitorEnabled` property.
- `Profiles/<profile>.json` stores candidates, sections, layout, monitor settings, and monitor anchors.
- `Logs/app.log` contains the current session log, and `erin-timer.json` stores Erin timer settings separately from overlay profiles.
- On first launch, portable data beside the executable is copied into LocalAppData without overwriting existing destination files or deleting the originals.
- Missing or invalid settings fall back to defaults.
- `AtomicJsonFile` writes settings and profiles through a same-directory temporary file, flushes it to disk, replaces the primary file, and maintains a `.bak` recovery copy.
- Profile loading can recover from an invalid primary file or a newer valid backup. Save failures must remain visible to callers so profile switches and process shutdown cannot silently discard dirty state.

## Recent Fixes

The current `develop` baseline includes the profile, tray, guide, runtime, alert-sound, layout, and monitor improvements from `feature/settings-tray-exit`.

- Settings and profiles use atomic primary/backup persistence.
- Portable profile packages copy owned audio assets into the package.
- The close button can exit, minimize to the tray, or ask the user.
- The current stabilization branch propagates profile save failures so profile switching and actual process exit cannot silently discard dirty state.
- Overlay startup serialization, WGC resize handling, and WGC target-close handling are the current runtime stabilization focus.
- OCR diagnostic image capture is opt-in, profile save failures retry automatically, and a failed save can be recovered by choosing a new profile folder.
- Normal launches are single-instance; a second launch activates the existing window.

## Known Technical Risks

1. `MainWindow.xaml.cs` is a large orchestration file. New work should avoid adding more unrelated state directly there when a focused service/controller can own it.
2. DXGI currently creates capture resources for each captured frame. At high FPS this is materially more expensive than WGC's persistent frame session.
3. CPU render paths copy full frames into managed memory before cropping or compositing. This can be expensive at high resolution.
4. Buff times use one batch OCR pass over the time column. Missing or ambiguous rows fall back to sequential row OCR, so repeated fallbacks can still extend the effective interval.
5. Monitor OCR and template matching require user runtime validation across UI scale, map brightness, and installed Windows OCR language packs.
6. Existing legacy handoff and patch-note files were replaced or updated to avoid stale instructions. Keep future documentation in UTF-8.

## Recommended Next Work Order

1. Run and record every applicable row in the 0.0.5-beta manual release matrix, especially WGC resize/close, mixed DPI, profile-folder recovery, and second-instance activation.
2. Keep Release build and unit regression results separate from user-run Windows/game runtime verification.
3. Review accessibility names and keyboard behavior for the custom title bar and high-priority dialogs.
4. Measure DXGI resource churn, full-frame managed copies, high-FPS CPU rendering, and OCR fallback cadence before starting broad performance refactors.
5. Merge the verified stabilization work into `develop`, create `version/0.0.5-beta`, and perform signing/checksum/release work only from the reviewed release commit.

## September 7 defect fixes

- Desktop DXGI/GDI capture waits now run outside the UI thread, serialized by the capture coordinator. Results from superseded runtime options or capture sources are discarded. DXGI resource creation per capture and CPU compositing costs remain performance work.
- Monitor-only sessions surface capture/recognition failures and stop instead of silently retrying a failed source. WGC frames older than five seconds are no longer supplied to consumers. OCR availability is checked before monitoring starts.
- Both overlay windows share the recovered runtime position. Placement considers individual monitor rectangles, including gaps in staggered monitor arrangements. The saved position is not rewritten automatically.
- Valid backup data remains usable when repairing the original file fails. Profile UI reports the repair failure. JSON reads enforce a size limit, null collection entries trigger backup recovery, and explicit zero opacity survives profile application.
- Erin alarm changes remain pending after save failure, show a persistent warning, retry every five seconds, and require an explicit discard decision before normal process exit.
- Second-instance activation uses a named event, retaining requests sent before the first window is ready.
- Title bar actions have localized tooltips and accessibility names, including maximize/restore state.

Verification: Release build of the app and test project passed with zero warnings/errors; all 151 unit tests passed. No app GUI, game capture, screenshot probes, packaging, signing, or push was performed. Mixed-DPI placement, game-target lifecycle, cross-elevation activation and storage-permission UI behavior still require user runtime verification.
