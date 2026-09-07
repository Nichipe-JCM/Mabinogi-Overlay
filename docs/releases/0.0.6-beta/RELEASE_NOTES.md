# Mabinogi Overlay 0.0.6-beta

[Korean changelog](https://github.com/Nichipe-JCM/Mabinogi-Overlay/blob/0.0.6-beta/docs/releases/0.0.6-beta/CHANGELOG.ko.md)

This beta includes the stabilization and usability changes integrated since the 0.0.4.3 package.

## Capture and rendering

- Reuse DXGI capture resources and CPU composition buffers, and move desktop capture waits off the UI thread.
- Discard asynchronous results from stopped or superseded capture sessions.
- Improve WGC resize, target-close, and runtime error handling; reject WGC frames older than five seconds.
- Check OCR availability before monitoring and surface capture/recognition failures.
- Recover off-screen overlay placement using actual monitor bounds and share the recovered position with timer overlays.

## Saving and recovery

- Keep valid backups usable even when repairing the primary file fails.
- Show and retry failed profile and Erin alarm saves, with explicit handling before profile switches or exit.
- Validate profile JSON, package/audio inputs, and profile names; preserve zero-opacity settings.

## Usability

- Activate the existing window on a second launch, including requests made before startup finishes.
- Improve tray controls, settings organization, grouped layout movement, scaling, and monitor controls.
- Add Hamjji multi-icon detection, replace the default alert sound, and localize title-bar accessibility labels.
- Keep OCR diagnostic screenshots opt-in. Interactive test tools and fault injection are excluded from the product branch and package.

## Download and verification

Download `MabinogiOverlay-0.0.6-beta-win-x64-portable.zip`, extract the entire archive, and run `Mabinogi Overlay.exe`. The .NET runtime is included. Close the previous version before updating; existing data remains under `%LocalAppData%\Mabinogi Overlay` by default.

- Windows x64, self-contained SingleFile and ReadyToRun build.
- 158 automated tests pass; Release build and packaging verified.
- The executable is signed with the project's temporary self-signed certificate and timestamped. Windows may still display a certificate trust warning.
- SHA-256 checksums are provided in `SHA256SUMS.txt`.

## Known limitations

Actual game capture, GPU fallback, resize/minimize/restart, buff recognition, hotkeys, and mixed-DPI behavior still require environment-specific manual verification. Guide screenshots are deferred to a later version; the text guide remains available.
