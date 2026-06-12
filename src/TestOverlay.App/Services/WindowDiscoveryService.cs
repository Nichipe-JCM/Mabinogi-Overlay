using System.Diagnostics;
using System.IO;
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
            var processInfo = GetProcessInfo(pid);
            windows.Add(new GameWindowInfo(
                hWnd,
                title,
                processInfo.ProcessName,
                processInfo.ExecutableName,
                rect.Width,
                rect.Height));
            return true;
        }, 0);

        return windows
            .OrderByDescending(window => window.IsExactClientExecutable)
            .ThenByDescending(window => window.IsPreferredMabinogiClient)
            .ThenByDescending(window => window.LooksLikeMabinogi)
            .ThenBy(window => window.Title)
            .ToList();
    }

    private static ProcessInfo GetProcessInfo(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            var processName = process.ProcessName;
            var executableName = GetExecutableName(process, processName);
            return new ProcessInfo(processName, executableName);
        }
        catch
        {
            return new ProcessInfo("unknown", "unknown");
        }
    }

    private static string GetExecutableName(Process process, string processName)
    {
        try
        {
            var fileName = process.MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                return Path.GetFileName(fileName);
            }
        }
        catch
        {
            // Some elevated or protected windows do not allow MainModule access.
        }

        return processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName
            : $"{processName}.exe";
    }

    private sealed record ProcessInfo(string ProcessName, string ExecutableName);
}
