using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace TestOverlay.App.Services;

public sealed class AppLog : IDisposable
{
    private const long DefaultMaximumLogBytes = 5 * 1024 * 1024;
    private const int DefaultRetainedLogFiles = 4;
    private const int QueueCapacity = 4096;
    private static readonly object IoSync = new();
    private readonly bool _enabled;
    private readonly long _maximumLogBytes;
    private readonly int _retainedLogFiles;
    private readonly BlockingCollection<string>? _queue;
    private readonly Task? _writerTask;
    private int _droppedMessages;
    private bool _disposed;

    public AppLog(
        string? logDirectory = null,
        bool enabled = true,
        long maximumLogBytes = DefaultMaximumLogBytes,
        int retainedLogFiles = DefaultRetainedLogFiles)
    {
        _enabled = enabled;
        _maximumLogBytes = Math.Max(64 * 1024, maximumLogBytes);
        _retainedLogFiles = Math.Clamp(retainedLogFiles, 1, 10);
        if (enabled && logDirectory is null)
        {
            AppDataPaths.EnsureInitialized();
        }
        LogDirectory = logDirectory ?? (enabled ? AppDataPaths.LogDirectory : Path.GetTempPath());
        if (_enabled)
        {
            _queue = new BlockingCollection<string>(QueueCapacity);
            _writerTask = Task.Factory.StartNew(
                ProcessQueue,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }
    }

    public string LogDirectory { get; }

    public string LogPath => Path.Combine(LogDirectory, "app.log");

    public DateTimeOffset SessionStartedAt { get; } = DateTimeOffset.Now;

    public void Info(string message) => Write("INFO", message);

    public void Error(string message, Exception exception) =>
        Write("ERROR", $"{message}{Environment.NewLine}{exception}");

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue?.CompleteAdding();
        var writerCompleted = false;
        try
        {
            writerCompleted = _writerTask?.Wait(TimeSpan.FromSeconds(3)) != false;
        }
        catch
        {
            // Logging must never prevent application shutdown.
        }
        if (writerCompleted)
        {
            _queue?.Dispose();
        }
    }

    private void Write(string level, string message)
    {
        if (!_enabled || _disposed || _queue is null)
        {
            return;
        }

        var entry = $"[{DateTimeOffset.Now:O}] {level} {RedactLocalPaths(message)}{Environment.NewLine}";
        if (!_queue.TryAdd(entry))
        {
            Interlocked.Increment(ref _droppedMessages);
        }
    }

    private void ProcessQueue()
    {
        if (_queue is null)
        {
            return;
        }

        foreach (var entry in _queue.GetConsumingEnumerable())
        {
            try
            {
                var dropped = Interlocked.Exchange(ref _droppedMessages, 0);
                var output = dropped == 0
                    ? entry
                    : $"[{DateTimeOffset.Now:O}] WARN Log queue overflow: dropped={dropped}.{Environment.NewLine}{entry}";
                WriteEntry(output);
            }
            catch
            {
                // A logging failure must not terminate the writer or the application.
            }
        }
    }

    private void WriteEntry(string entry)
    {
        lock (IoSync)
        {
            Directory.CreateDirectory(LogDirectory);
            RotateIfNeeded(Encoding.UTF8.GetByteCount(entry));
            File.AppendAllText(LogPath, entry, new UTF8Encoding(false));
        }
    }

    private void RotateIfNeeded(int incomingBytes)
    {
        if (!File.Exists(LogPath) || new FileInfo(LogPath).Length + incomingBytes <= _maximumLogBytes)
        {
            return;
        }

        var oldest = RotatedLogPath(_retainedLogFiles);
        File.Delete(oldest);
        for (var index = _retainedLogFiles - 1; index >= 1; index--)
        {
            var source = RotatedLogPath(index);
            if (File.Exists(source))
            {
                File.Move(source, RotatedLogPath(index + 1));
            }
        }

        File.Move(LogPath, RotatedLogPath(1));
    }

    private string RotatedLogPath(int index) => Path.Combine(LogDirectory, $"app.{index}.log");

    private static string RedactLocalPaths(string message)
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            message = message.Replace(localAppData, "%LOCALAPPDATA%", StringComparison.OrdinalIgnoreCase);
        }
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            message = message.Replace(userProfile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }

        return message;
    }
}
