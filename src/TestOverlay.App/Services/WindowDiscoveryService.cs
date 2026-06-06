using System.Diagnostics;
using System.Text;
using TestOverlay.App.Models;
using TestOverlay.App.Native;

namespace TestOverlay.App.Services;

public sealed class WindowDiscoveryService
{
    public IReadOnlyList<GameWindowInfo> GetVisibleWindows()
    {
        var windows = new List<GameWindowInfo>();

        Win32Methods.EnumWindows((hWnd, _) =>
        {
            if (!Win32Methods.IsWindowVisible(hWnd))
            {
                return true;
            }

            var textLength = Win32Methods.GetWindowTextLength(hWnd);
            if (textLength <= 0)
            {
                return true;
            }

            var titleBuilder = new StringBuilder(textLength + 1);
            Win32Methods.GetWindowText(hWnd, titleBuilder, titleBuilder.Capacity);
            var title = titleBuilder.ToString();
            if (string.IsNullOrWhiteSpace(title))
            {
                return true;
            }

            if (!Win32Methods.GetClientRect(hWnd, out var rect) || rect.Width < 320 || rect.Height < 240)
            {
                return true;
            }

            Win32Methods.GetWindowThreadProcessId(hWnd, out var pid);
            var processName = GetProcessName(pid);
            windows.Add(new GameWindowInfo(hWnd, title, processName, rect.Width, rect.Height));
            return true;
        }, 0);

        return windows
            .OrderByDescending(window => window.LooksLikeMabinogi)
            .ThenBy(window => window.Title)
            .ToList();
    }

    private static string GetProcessName(uint pid)
    {
        try
        {
            return Process.GetProcessById((int)pid).ProcessName;
        }
        catch
        {
            return "unknown";
        }
    }
}
