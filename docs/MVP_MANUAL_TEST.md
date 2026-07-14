# Manual Verification Checklist

Run this checklist on the target Windows machine with Mabinogi in windowed or borderless-windowed mode. The user performs runtime verification; automated build verification alone does not confirm capture accuracy or click-through behavior.

## Build

```powershell
dotnet build src\TestOverlay.App\TestOverlay.App.csproj
```

Expected: zero build errors.

## Compact mode

1. Finish capture, monitor, and layout configuration in the full window.
2. Open Compact mode and verify that the full window is hidden and the compact window shows the active profile and overlay state.
3. Start and stop the overlay from the compact window and verify that the state indicator and button label update.
4. Change a previously recognized buff selection while the overlay is running and verify that the overlay restarts and monitoring continues.
5. Open Manage Layout and verify that the full window appears only while the modal editor is open, then returns to Compact mode.
6. Open Erin Timer and verify that the full window returns with the Erin Timer tab selected.
7. Restart the application from Compact mode and verify that Compact mode is restored.

## Capture and quickslot workflow

1. Open Mabinogi and make the intended quickslot sections visible.
2. Start the app and confirm the exact `Client.exe` entry is prioritized in the window list.
3. Run `Auto capture`. If it cannot find the game, run `Manual capture` and choose the game window.
4. Confirm that the preview shows the expected client image.
5. Click `Auto detect section`, then drag an ROI around one horizontal or vertical quickslot section.
6. Confirm that detected candidates remain inside the selected ROI and are added as a new section rather than replacing previous sections.
7. Correct candidates with selection, drag, arrow-key nudging, manual add/delete, or manual section controls.
8. Select desired candidates and use `Add to overlay`.

Expected:

- Existing sections and overlay slots remain intact when a new section is detected.
- Candidate selection, multi-selection, undo/redo, and deletion remain usable.
- Candidate source rectangles match the intended slot interior after calibration.

## Layout and quickslot overlay

1. Open `Manage Layout`.
2. Drag slots, use grid snap, test multi-selection, and adjust canvas size, global scale, opacity, and slot overrides.
3. Open screen preview and place the overlay on the target monitor.
4. Apply and close the editor.
5. Choose a capture backend in Settings and leave automatic renderer selection enabled.
6. Click `Overlay start`.
7. Click through the visible overlay onto the game and verify the game retains focus.
8. Verify that the configured stop hotkey and `Overlay stop` both end the session.

Expected:

- Overlay is always on top, click-through, and non-activating.
- Quickslots refresh from the selected live capture path.
- Automatic rendering uses GPU acceleration with WGC and CPU compositing with DXGI monitor or GDI. Manual renderer overrides are available under Advanced renderer override for troubleshooting.
- If GPU initialization fails, verify that the runtime log reports a CPU/Composited fallback and that the overlay continues refreshing.

## Buff and Tuairim monitor

1. Open the Buff/Tuairim tab.
2. Enable buff monitoring, detect the buff window ROI, and verify the discovered buff choices.
3. Enable Tuairim monitoring and detect its UI ROI.
4. Select desired buff entries, thresholds, alert frequency, sounds, and volumes.
5. Add the monitor elements to the layout and start the overlay.
6. Observe a normal timer decrement, a buff refresh, a buff disappearance, a Tuairim increase, and a Tuairim reset when available.

Expected:

- A single unreadable OCR frame does not immediately remove a buff or reset its timer.
- Buff expiry requires repeated confirmation.
- Tuairim stays within 0-100 and does not accept an unconfirmed decrease.
- Alert sound and visual notification fire at the configured threshold.

## Erin timer

1. Open the Erin Timer tab.
2. Create a named alarm, select its time, and enable it.
3. Toggle the alarm off and on, then switch tabs.
4. Close and reopen the application.

Expected:

- Enabled state changes the next-alarm summary immediately.
- Disabled alarms are not scheduled.
- Alarm configuration persists after reopening the app.

## Logs and bug reports

When a failure occurs, use Settings > Log and collect `%LocalAppData%\Mabinogi Overlay\Logs\app.log`. For detection or OCR issues, include the selected capture backend, renderer, game resolution/UI scale, a screenshot if possible, and the steps that caused the issue.

## Upgrade compatibility

1. Start with a pre-automatic-renderer `settings.json` using WGC + GPU/DXGI and verify it migrates to automatic GPU acceleration.
2. Start with WGC + CPU/WPF and verify the manual compatibility override is preserved.
3. Load a profile containing the legacy `TuarimMonitorEnabled` property and verify the Tuairim monitor remains enabled.
4. Save the migrated profile and verify it contains `TuairimMonitorEnabled` but not the legacy misspelled property.
5. Verify that `settings.json.bak` and profile `.json.bak` recovery still works after migration.
6. For a side-by-side portable upgrade, leave the legacy `settings.json`, `save`, and `Logs` beside the old executable and verify that first launch copies missing files into LocalAppData without overwriting existing user data.

## Monitor capture performance

1. Run WGC + automatic GPU rendering with buff monitoring enabled.
2. Compare overlay smoothness and CPU usage with one and four selected buffs.
3. Verify buff activation and expiration still update within the expected recognition delay.
4. Check the log for OCR failures and confirm diagnostic images are written only once per failure kind.
