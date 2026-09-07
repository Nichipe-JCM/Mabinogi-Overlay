using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows.Interop;
using System.Windows.Threading;
using TestOverlay.App.Native;

namespace TestOverlay.App.Services;

public sealed class HotkeyService : IDisposable
{
    private const int DefaultHotkeyId = 0x3141;
    private readonly AppLog _log;
    private HwndSource? _source;
    private nint _windowHandle;
    private bool _messagePathObserved;
    private readonly Dictionary<int, Action> _callbacks = [];
    private readonly Dictionary<int, PolledHotkey> _polledHotkeys = [];
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromMilliseconds(25) };

    public HotkeyService(AppLog log)
    {
        _log = log;
        _pollTimer.Tick += PollTimer_Tick;
        _log.Info($"Hotkey service initialized: processElevated={IsCurrentProcessElevated()}, pollIntervalMs={_pollTimer.Interval.TotalMilliseconds:0}.");
    }

    public bool Register(
        nint windowHandle,
        uint modifiers,
        uint virtualKey,
        Action callback,
        string label = "hotkey") =>
        Register(windowHandle, DefaultHotkeyId, modifiers, virtualKey, callback, label);

    public bool Register(
        nint windowHandle,
        int id,
        uint modifiers,
        uint virtualKey,
        Action callback,
        string label = "hotkey")
    {
        if (_windowHandle == nint.Zero)
        {
            _windowHandle = windowHandle;
            _source = HwndSource.FromHwnd(windowHandle);
            _source?.AddHook(WndProc);
            if (_source is null)
            {
                _log.Info(
                    $"Hotkey message hook unavailable: hwnd=0x{windowHandle.ToInt64():X}, " +
                    $"id=0x{id:X}, label={label}. Polling may be the only active input path.");
            }
        }
        else if (_windowHandle != windowHandle)
        {
            throw new InvalidOperationException("All hotkeys in a registry must use the same window handle.");
        }

        if (_callbacks.ContainsKey(id))
        {
            _log.Info($"Hotkey registration rejected: duplicate id=0x{id:X}, label={label}.");
            return false;
        }

        Marshal.SetLastPInvokeError(0);
        if (!Win32Methods.RegisterHotKey(
                windowHandle,
                id,
                modifiers | Win32Methods.ModNoRepeat,
                virtualKey))
        {
            var error = Marshal.GetLastPInvokeError();
            var errorMessage = error == 0 ? "unknown" : new Win32Exception(error).Message;
            _log.Info(
                $"Hotkey registration failed: id=0x{id:X}, label={label}, " +
                $"modifiers=0x{modifiers:X}, virtualKey=0x{virtualKey:X}, " +
                $"hwnd=0x{windowHandle.ToInt64():X}, win32Error={error} ({errorMessage}).");
            return false;
        }

        _callbacks[id] = callback;
        _polledHotkeys[id] = new PolledHotkey(modifiers, virtualKey, label);
        _pollTimer.Start();
        _log.Info(
            $"Hotkey registered: id=0x{id:X}, label={label}, modifiers=0x{modifiers:X}, " +
            $"virtualKey=0x{virtualKey:X}, hwnd=0x{windowHandle.ToInt64():X}, " +
            $"messageHook={_source is not null}, polling=True.");
        return true;
    }

    public void Dispose()
    {
        _pollTimer.Stop();
        if (_windowHandle != nint.Zero)
        {
            foreach (var id in _callbacks.Keys)
            {
                Marshal.SetLastPInvokeError(0);
                if (!Win32Methods.UnregisterHotKey(_windowHandle, id))
                {
                    var error = Marshal.GetLastPInvokeError();
                    _log.Info($"Hotkey unregister failed: id=0x{id:X}, win32Error={error}.");
                }
            }
        }

        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            _source = null;
        }
        _windowHandle = nint.Zero;
        _callbacks.Clear();
        _polledHotkeys.Clear();
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == Win32Methods.WmHotkey && _callbacks.TryGetValue(wParam.ToInt32(), out var callback))
        {
            if (!_messagePathObserved)
            {
                _messagePathObserved = true;
                _log.Info("WM_HOTKEY delivery confirmed; periodic key-state polling disabled for this session.");
            }
            InvokeCallback(wParam.ToInt32(), callback, "WM_HOTKEY");
            handled = true;
        }

        return 0;
    }

    private void PollTimer_Tick(object? sender, EventArgs e)
    {
        if (_messagePathObserved)
        {
            return;
        }

        foreach (var (id, hotkey) in _polledHotkeys)
        {
            var isDown = IsDown(hotkey.VirtualKey) && ModifiersAreDown(hotkey.Modifiers);
            if (isDown && !hotkey.WasDown && _callbacks.TryGetValue(id, out var callback))
            {
                InvokeCallback(id, callback, "GetAsyncKeyState");
            }
            hotkey.WasDown = isDown;
        }
    }

    private void InvokeCallback(int id, Action callback, string source)
    {
        if (_polledHotkeys.TryGetValue(id, out var hotkey))
        {
            var now = Environment.TickCount64;
            if (now - hotkey.LastInvokedAt < 120)
            {
                return;
            }
            hotkey.LastInvokedAt = now;
            _log.Info($"Hotkey invoked: id=0x{id:X}, label={hotkey.Label}, source={source}.");
        }
        callback();
    }

    private static bool ModifiersAreDown(uint modifiers) =>
        ((modifiers & Win32Methods.ModControl) == 0 || IsDown(0x11)) &&
        ((modifiers & Win32Methods.ModShift) == 0 || IsDown(0x10)) &&
        ((modifiers & Win32Methods.ModAlt) == 0 || IsDown(0x12)) &&
        ((modifiers & Win32Methods.ModWin) == 0 || IsDown(0x5B) || IsDown(0x5C));

    private static bool IsDown(uint virtualKey) =>
        (Win32Methods.GetAsyncKeyState((int)virtualKey) & 0x8000) != 0;

    private static bool IsCurrentProcessElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private sealed class PolledHotkey(uint modifiers, uint virtualKey, string label)
    {
        public uint Modifiers { get; } = modifiers;
        public uint VirtualKey { get; } = virtualKey;
        public string Label { get; } = label;
        public bool WasDown { get; set; }
        public long LastInvokedAt { get; set; } = -1000;
    }
}
