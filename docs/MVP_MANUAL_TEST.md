# MVP Manual Test Checklist

Use this checklist on the target Windows machine with Mabinogi running in windowed or borderless-windowed mode.

## Build

```powershell
dotnet build MabinogiOverlay.sln
```

Expected result:

- Build succeeds with zero errors.

## Window Verification and Capture

1. Run the app.
2. Click `Refresh`.
3. Confirm a Mabinogi-like window appears in the game window list.
4. Click `Verify WGC`.
5. In the Windows Graphics Capture picker, choose the Mabinogi game window.
6. Confirm the status text says WGC verified a Mabinogi window.
7. Click `Capture`.

Expected result:

- The capture preview shows the selected game window.
- Capture should not proceed before WGC verification.

## Slot Candidate Flow

1. Adjust min and max slot size if needed.
2. Click `Detect Slots`.
3. Review candidate rectangles over the capture.
4. Check only the slots that should appear in the overlay.
5. Click `Place Selected`.

Expected result:

- Selected slots appear in the overlay canvas preview.
- The user can drag placed slots freely inside the canvas.

## Overlay Flow

1. Set overlay width and height.
2. Set screen X and Y, or click `Default Pos`.
3. Set opacity.
4. Set a stop hotkey such as `Ctrl+Shift+F8`.
5. Click `Start`.

Expected result:

- The overlay appears always on top.
- The overlay is click-through and does not consume mouse clicks.
- The base application window remains normal, not always-on-top.
- The overlay contents refresh from the live game window.

## Stop and Restore

1. Press the configured stop hotkey.
2. Start again.
3. Click `Stop Overlay`.

Expected result:

- Both the hotkey and the base UI button close the overlay.
- The base UI remains open and usable.

## Profile

1. After placing slots, click `Save`.
2. Restart or clear the current layout.
3. Capture the game window again.
4. Click `Load`.

Expected result:

- Canvas size, screen position, opacity, hotkey, and slot layout are restored.
