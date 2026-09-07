# Mabinogi Overlay Planning

## Product Boundary

Mabinogi Overlay improves visual readability by copying user-selected game UI regions into a separate Windows overlay. It is a display utility, not a game automation tool.

Allowed implementation areas:

- Windows Graphics Capture, DXGI Desktop Duplication, and GDI desktop capture
- Separate transparent click-through overlay windows
- Local profiles, local logs, global stop hotkeys, image analysis, and OCR

Out of scope:

- Game memory inspection
- Process or DLL injection
- Game renderer hooks
- Gameplay input automation
- Anti-cheat bypass, concealment, packet capture, or traffic interception

## Implemented Product Areas

### Quickslot overlay

1. The user captures the Mabinogi client.
2. The user draws an ROI around a quickslot section.
3. The detector chooses a horizontal or vertical pattern from ROI shape and creates editable candidates.
4. The user corrects candidates and places selected slots on an overlay canvas.
5. The running overlay refreshes the selected source rectangles and remains click-through.

The calibration editor is the core feature. Automatic detection is an assistive starting point, not an authority over the final layout.

### Status monitoring

The active feature branch adds an optional status monitor for four bard buffs and the Tuairim gauge.

- Known buff icons and the Tuairim UI are located from bundled templates.
- Buff time and Tuairim percent are read with Windows OCR and image-mask fallbacks.
- State guards protect against isolated OCR failures, sudden timer drops, false zero values, and invalid Tuairim jumps.
- The user can choose monitored buffs, thresholds, sound mode, per-sound volume, and visual alert behavior.

### Erin timer

The Erin timer is integrated as a main-tab tool. It tracks current Erin time and keeps persistent alarms alive while the tab is not selected. Alarm enable/repeat state and optional custom sound configuration are stored locally.

## Architecture Direction

The current application is WPF/.NET 8. Its main technical challenge is reliable calibration and a stable overlay session rather than raw game integration.

Preferred live path:

```text
WGC item -> persistent frame pool -> GPU/DXGI renderer -> DirectComposition overlay
```

Compatibility paths:

```text
DXGI monitor crop or GDI client crop -> CPU renderer -> WPF overlay
```

The GPU/WGC path should remain the performance reference. Future DXGI work should retain duplication resources between frames rather than creating them per frame.

## Persistence Model

Profiles store capture-pixel coordinates, candidates, sections, overlay positions, monitor ROIs, monitor selections, and alert configuration. Profiles are intentionally tied to a compatible game UI scale and capture resolution.

Global settings store profile directory, language, capture backend, and renderer selection under the current user's LocalAppData directory. Legacy portable data is imported on first launch.

## Next Technical Priorities

1. Use user runtime feedback to stabilize status-monitor recognition.
2. Reduce CPU/DXGI live capture overhead.
3. Split `MainWindow` orchestration into focused services or controllers before adding another major feature.
4. Use atomic writes for profile and settings files.
5. Maintain English and Korean localization entries together with every UI change.

See [CODEX_PROJECT_HANDOFF.md](CODEX_PROJECT_HANDOFF.md) for the current repository and branch state.
