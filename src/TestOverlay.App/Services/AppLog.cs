using System.IO;

namespace TestOverlay.App.Services;

public sealed class AppLog
{
    private static readonly object Sync = new();
    private readonly bool _enabled;

    public AppLog(string? logDirectory = null, bool enabled = true)
    {
        _enabled = enabled;
        if (enabled && logDirectory is null)
        {
            AppDataPaths.EnsureInitialized();
        }
        LogDirectory = logDirectory ?? (enabled ? AppDataPaths.LogDirectory : Path.GetTempPath());
    }

    public string LogDirectory { get; }

    public string LogPath => Path.Combine(LogDirectory, "app.log");

    public DateTimeOffset SessionStartedAt { get; } = DateTimeOffset.Now;

    public void Info(string message) => Write("INFO", message);

    public void Error(string message, Exception exception) =>
        Write("ERROR", $"{message}{Environment.NewLine}{exception}");

    private void Write(string level, string message)
    {
        if (!_enabled)
        {
            return;
        }

        lock (Sync)
        {
            Directory.CreateDirectory(LogDirectory);
            File.AppendAllText(LogPath, $"[{DateTimeOffset.Now:O}] {level} {message}{Environment.NewLine}");
        }
    }
}
