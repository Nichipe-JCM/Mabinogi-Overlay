using System.Threading;
using System.Windows;
using System.Windows.Interop;
using TestOverlay.App.Native;

namespace TestOverlay.App.Services;

public sealed class SingleInstanceCoordinator : IDisposable
{
    private const string MutexName = @"Local\Nichipe.MabinogiOverlay";
    private const string ActivationMessageName = "Nichipe.MabinogiOverlay.Activate";
    private Mutex? _mutex;
    private HwndSource? _source;
    private bool _ownsMutex;
    private uint _activationMessage;

    public bool TryAcquirePrimary()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        _ownsMutex = createdNew;
        return createdNew;
    }

    public void SignalPrimaryInstance()
    {
        var message = ActivationMessage();
        if (message != 0)
        {
            Win32Methods.PostMessage(Win32Methods.HwndBroadcast, message, nint.Zero, nint.Zero);
        }
    }

    public void Attach(Window window, Action activate)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(activate);
        _source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
        _source?.AddHook(Hook);
        return;

        nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
        {
            if ((uint)message != ActivationMessage())
            {
                return nint.Zero;
            }

            handled = true;
            window.Dispatcher.BeginInvoke(activate);
            return nint.Zero;
        }
    }

    public void Dispose()
    {
        if (_source is not null)
        {
            // The HwndSource is owned by WPF. It removes all hooks when the
            // window closes, so do not dispose it here.
            _source = null;
        }

        if (_ownsMutex)
        {
            try
            {
                _mutex?.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // The process is already shutting down or ownership was lost.
            }
        }

        _mutex?.Dispose();
        _mutex = null;
        _ownsMutex = false;
    }

    private uint ActivationMessage() =>
        _activationMessage != 0
            ? _activationMessage
            : _activationMessage = Win32Methods.RegisterWindowMessage(ActivationMessageName);
}
