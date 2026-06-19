using System.IO;

namespace TestOverlay.App.Services;

public sealed class AppLog
{
    private readonly object _sync = new();

    public string LogDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "Logs");

    public string LogPath => Path.Combine(LogDirectory, "app.log");

    public DateTimeOffset SessionStartedAt { get; } = DateTimeOffset.Now;

    public void Info(string message) => Write("INFO", message);

    public void Error(string message, Exception exception) =>
        Write("ERROR", $"{message}{Environment.NewLine}{exception}");

    private void Write(string level, string message)
    {
        lock (_sync)
        {
            Directory.CreateDirectory(LogDirectory);
            File.AppendAllText(LogPath, $"[{DateTimeOffset.Now:O}] {level} {message}{Environment.NewLine}");
        }
    }
}
