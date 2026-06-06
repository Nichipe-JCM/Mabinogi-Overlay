using System.Windows.Interop;
using TestOverlay.App.Native;

namespace TestOverlay.App.Services;

public sealed class HotkeyService : IDisposable
{
    private const int HotkeyId = 0x3141;
    private HwndSource? _source;
    private Action? _callback;

    public bool Register(nint windowHandle, uint modifiers, uint virtualKey, Action callback)
    {
        Dispose();
        _callback = callback;
        _source = HwndSource.FromHwnd(windowHandle);
        _source?.AddHook(WndProc);
        return Win32Methods.RegisterHotKey(windowHandle, HotkeyId, modifiers, virtualKey);
    }

    public void Dispose()
    {
        if (_source is not null)
        {
            Win32Methods.UnregisterHotKey(_source.Handle, HotkeyId);
            _source.RemoveHook(WndProc);
            _source = null;
        }
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == Win32Methods.WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            _callback?.Invoke();
            handled = true;
        }

        return 0;
    }
}
