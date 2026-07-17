using System.Windows.Interop;
using TestOverlay.App.Native;

namespace TestOverlay.App.Services;

public sealed class HotkeyService : IDisposable
{
    private const int DefaultHotkeyId = 0x3141;
    private HwndSource? _source;
    private readonly Dictionary<int, Action> _callbacks = [];

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

        if (_callbacks.ContainsKey(id) || !Win32Methods.RegisterHotKey(windowHandle, id, modifiers, virtualKey))
        {
            return false;
        }

        _callbacks[id] = callback;
        return true;
    }

    public void Dispose()
    {
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
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == Win32Methods.WmHotkey && _callbacks.TryGetValue(wParam.ToInt32(), out var callback))
        {
            callback();
            handled = true;
        }

        return 0;
    }
}
