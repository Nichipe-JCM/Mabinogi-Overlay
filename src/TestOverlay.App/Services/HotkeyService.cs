using System.Windows.Interop;
using TestOverlay.App.Native;

namespace TestOverlay.App.Services;

public sealed class HotkeyService : IDisposable
{
    private HwndSource? _source;
    private Action? _callback;

    public void Register(nint windowHandle, Action callback)
    {
        _callback = callback;
        _source = HwndSource.FromHwnd(windowHandle);
        _source?.AddHook(WndProc);
        Win32Methods.RegisterHotKey(windowHandle, Win32Methods.HotkeyId, Win32Methods.ModControl | Win32Methods.ModShift, Win32Methods.VkF8);
    }

    public void Dispose()
    {
        if (_source is not null)
        {
            Win32Methods.UnregisterHotKey(_source.Handle, Win32Methods.HotkeyId);
            _source.RemoveHook(WndProc);
            _source = null;
        }
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == Win32Methods.WmHotkey && wParam.ToInt32() == Win32Methods.HotkeyId)
        {
            _callback?.Invoke();
            handled = true;
        }

        return 0;
    }
}
