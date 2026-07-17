using System.Windows.Interop;
using System.Windows.Threading;
using TestOverlay.App.Native;

namespace TestOverlay.App.Services;

public sealed class HotkeyService : IDisposable
{
    private const int DefaultHotkeyId = 0x3141;
    private HwndSource? _source;
    private readonly Dictionary<int, Action> _callbacks = [];
    private readonly Dictionary<int, PolledHotkey> _polledHotkeys = [];
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromMilliseconds(25) };

    public HotkeyService()
    {
        _pollTimer.Tick += PollTimer_Tick;
    }

    public bool Register(nint windowHandle, uint modifiers, uint virtualKey, Action callback)
        => Register(windowHandle, DefaultHotkeyId, modifiers, virtualKey, callback);

    public bool Register(nint windowHandle, int id, uint modifiers, uint virtualKey, Action callback)
    {
        if (_source is null)
        {
            _source = HwndSource.FromHwnd(windowHandle);
            _source?.AddHook(WndProc);
        }
        else if (_source.Handle != windowHandle)
        {
            throw new InvalidOperationException("All hotkeys in a registry must use the same window handle.");
        }

        if (_callbacks.ContainsKey(id) ||
            !Win32Methods.RegisterHotKey(windowHandle, id, modifiers | Win32Methods.ModNoRepeat, virtualKey))
        {
            return false;
        }

        _callbacks[id] = callback;
        _polledHotkeys[id] = new PolledHotkey(modifiers, virtualKey);
        _pollTimer.Start();
        return true;
    }

    public void Dispose()
    {
        _pollTimer.Stop();
        if (_source is not null)
        {
            foreach (var id in _callbacks.Keys)
            {
                Win32Methods.UnregisterHotKey(_source.Handle, id);
            }
            _source.RemoveHook(WndProc);
            _source = null;
        }
        _callbacks.Clear();
        _polledHotkeys.Clear();
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == Win32Methods.WmHotkey && _callbacks.TryGetValue(wParam.ToInt32(), out var callback))
        {
            InvokeCallback(wParam.ToInt32(), callback);
            handled = true;
        }

        return 0;
    }

    private void PollTimer_Tick(object? sender, EventArgs e)
    {
        foreach (var (id, hotkey) in _polledHotkeys)
        {
            var isDown = IsDown(hotkey.VirtualKey) && ModifiersAreDown(hotkey.Modifiers);
            if (isDown && !hotkey.WasDown && _callbacks.TryGetValue(id, out var callback))
            {
                InvokeCallback(id, callback);
            }
            hotkey.WasDown = isDown;
        }
    }

    private void InvokeCallback(int id, Action callback)
    {
        if (_polledHotkeys.TryGetValue(id, out var hotkey))
        {
            var now = Environment.TickCount64;
            if (now - hotkey.LastInvokedAt < 120)
            {
                return;
            }
            hotkey.LastInvokedAt = now;
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

    private sealed class PolledHotkey(uint modifiers, uint virtualKey)
    {
        public uint Modifiers { get; } = modifiers;
        public uint VirtualKey { get; } = virtualKey;
        public bool WasDown { get; set; }
        public long LastInvokedAt { get; set; } = -1000;
    }
}
