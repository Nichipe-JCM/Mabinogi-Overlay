using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using TestOverlay.App.Services;

namespace TestOverlay.App;

public partial class LogWindow : Window
{
    private readonly string _logPath;
    private readonly DateTimeOffset _sessionStartedAt;

    public LogWindow(string logPath, DateTimeOffset sessionStartedAt)
    {
        InitializeComponent();
        _logPath = logPath;
        _sessionStartedAt = sessionStartedAt;
        LogPathText.Text = logPath;
        LoadLog();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => LoadLog();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void LoadLog()
    {
        if (!File.Exists(_logPath))
        {
            LogSummaryText.Text = L.T("No log file exists for this session yet.");
            LogTextBox.Text = string.Empty;
            return;
        }

        try
        {
            var lines = ReadSessionLines();
            LogSummaryText.Text = L.F("Showing current session log since {0}. Lines: {1}", _sessionStartedAt.ToString("yyyy-MM-dd HH:mm:ss zzz"), lines.Count);
            LogTextBox.Text = string.Join(Environment.NewLine, lines);
            LogTextBox.CaretIndex = LogTextBox.Text.Length;
            LogTextBox.ScrollToEnd();
        }
        catch (Exception exception)
        {
            LogSummaryText.Text = L.T("Failed to read log file.");
            LogTextBox.Text = exception.ToString();
        }
    }

    private List<string> ReadSessionLines()
    {
        var lines = new List<string>();
        var includeContinuation = false;
        foreach (var path in EnumerateLogPathsOldestFirst())
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            while (reader.ReadLine() is { } line)
            {
                if (TryReadTimestamp(line, out var timestamp))
                {
                    includeContinuation = timestamp >= _sessionStartedAt;
                }

                if (includeContinuation)
                {
                    lines.Add(line);
                }
            }
        }

        return lines;
    }

    private IEnumerable<string> EnumerateLogPathsOldestFirst()
    {
        var directory = Path.GetDirectoryName(_logPath);
        var stem = Path.GetFileNameWithoutExtension(_logPath);
        var extension = Path.GetExtension(_logPath);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            foreach (var path in Directory
                         .EnumerateFiles(directory, $"{stem}.*{extension}")
                         .Select(path => (Path: path, Index: ReadRotationIndex(path, stem, extension)))
                         .Where(item => item.Index > 0)
                         .OrderByDescending(item => item.Index)
                         .Select(item => item.Path))
            {
                yield return path;
            }
        }

        if (File.Exists(_logPath))
        {
            yield return _logPath;
        }
    }

    private static int ReadRotationIndex(string path, string stem, string extension)
    {
        var fileName = Path.GetFileName(path);
        var prefix = $"{stem}.";
        return fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
               fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase) &&
               int.TryParse(fileName[prefix.Length..^extension.Length], out var index)
            ? index
            : -1;
    }

    private static bool TryReadTimestamp(string line, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (line.Length < 3 || line[0] != '[')
        {
            return false;
        }
        var close = line.IndexOf(']');
        if (close <= 1)
        {
            return false;
        }
        return DateTimeOffset.TryParse(
            line.Substring(1, close - 1),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out timestamp);
    }
}
