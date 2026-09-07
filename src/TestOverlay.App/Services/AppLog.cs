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
    private readonly string _primaryLogDirectory;
    private readonly string _fallbackLogDirectory;
    private readonly BlockingCollection<string>? _queue;
    private readonly Task? _writerTask;
    private string _activeLogDirectory;
    private int _droppedMessages;
    private bool _disposed;

    public AppLog(
        string? logDirectory = null,
        bool enabled = true,
        long maximumLogBytes = DefaultMaximumLogBytes,
        int retainedLogFiles = DefaultRetainedLogFiles,
        string? fallbackLogDirectory = null)
    {
        _enabled = enabled;
        _maximumLogBytes = Math.Max(64 * 1024, maximumLogBytes);
        _retainedLogFiles = Math.Clamp(retainedLogFiles, 1, 10);
        if (enabled && logDirectory is null)
        {
            try
            {
                AppDataPaths.EnsureInitialized();
            }
            catch
            {
                // The writer will switch to the fallback directory on its first entry.
            }
        }
        _primaryLogDirectory = logDirectory ?? (enabled ? AppDataPaths.LogDirectory : Path.GetTempPath());
        _fallbackLogDirectory = fallbackLogDirectory ??
                                Path.Combine(Path.GetTempPath(), "Mabinogi Overlay", "Logs");
        _activeLogDirectory = _primaryLogDirectory;
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

    public string LogDirectory => Volatile.Read(ref _activeLogDirectory);

    public string LogPath => Path.Combine(LogDirectory, "app.log");

    public DateTimeOffset SessionStartedAt { get; } = DateTimeOffset.Now;

    public void Info(string message) => Write("INFO", message);

    public void Error(string message, Exception exception) =>
        Write("ERROR", $"{message}{Environment.NewLine}{exception}");

    public string? WriteCritical(string message, Exception exception)
    {
        if (!_enabled)
        {
            return null;
        }

        var entry = FormatEntry("FATAL", $"{message}{Environment.NewLine}{exception}");
        try
        {
            WriteEntry(entry);
            return LogPath;
        }
        catch
        {
            return null;
        }
    }

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

        var entry = FormatEntry(level, message);
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
            var activeDirectory = LogDirectory;
            try
            {
                WriteEntryToDirectory(activeDirectory, entry);
            }
            catch (Exception primaryException) when (!PathsEqual(activeDirectory, _fallbackLogDirectory))
            {
                var fallbackNotice = FormatEntry(
                    "WARN",
                    $"Primary log unavailable; switched to fallback log. " +
                    $"primary={_primaryLogDirectory}{Environment.NewLine}{primaryException}");
                WriteEntryToDirectory(_fallbackLogDirectory, fallbackNotice + entry);
                Volatile.Write(ref _activeLogDirectory, _fallbackLogDirectory);
            }
        }
    }

    private void WriteEntryToDirectory(string directory, string entry)
    {
        Directory.CreateDirectory(directory);
        var logPath = Path.Combine(directory, "app.log");
        RotateIfNeeded(directory, logPath, Encoding.UTF8.GetByteCount(entry));
        File.AppendAllText(logPath, entry, new UTF8Encoding(false));
    }

    private void RotateIfNeeded(string directory, string logPath, int incomingBytes)
    {
        if (!File.Exists(logPath) || new FileInfo(logPath).Length + incomingBytes <= _maximumLogBytes)
        {
            return;
        }

        var oldest = RotatedLogPath(directory, _retainedLogFiles);
        File.Delete(oldest);
        for (var index = _retainedLogFiles - 1; index >= 1; index--)
        {
            var source = RotatedLogPath(directory, index);
            if (File.Exists(source))
            {
                File.Move(source, RotatedLogPath(directory, index + 1));
            }
        }

        File.Move(logPath, RotatedLogPath(directory, 1));
    }

    private static string RotatedLogPath(string directory, int index) =>
        Path.Combine(directory, $"app.{index}.log");

    private static string FormatEntry(string level, string message) =>
        $"[{DateTimeOffset.Now:O}] {level} {RedactLocalPaths(message)}{Environment.NewLine}";

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

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
