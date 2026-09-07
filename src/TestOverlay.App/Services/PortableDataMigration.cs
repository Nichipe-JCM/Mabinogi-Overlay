using System.IO;

namespace TestOverlay.App.Services;

public static class PortableDataMigration
{
    private const string MarkerFileName = ".portable-data-migrated-v1";

    public static PortableDataMigrationResult Migrate(string legacyRoot, string dataRoot)
    {
        var legacy = Path.GetFullPath(legacyRoot);
        var target = Path.GetFullPath(dataRoot);
        var profilesTarget = Path.Combine(target, "Profiles");
        var logsTarget = Path.Combine(target, "Logs");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(profilesTarget);
        Directory.CreateDirectory(logsTarget);

        var markerPath = Path.Combine(target, MarkerFileName);
        if (File.Exists(markerPath) || PathsEqual(legacy, target))
        {
            return new PortableDataMigrationResult(false, 0, 0, 0);
        }

        var settingsCopied = CopyIfMissing(
            Path.Combine(legacy, "settings.json"),
            Path.Combine(target, "settings.json"));
        CopyIfMissing(
            Path.Combine(legacy, "settings.json.bak"),
            Path.Combine(target, "settings.json.bak"));

        var profilesCopied = 0;
        var legacySave = Path.Combine(legacy, "save");
        if (Directory.Exists(legacySave))
        {
            foreach (var source in Directory.EnumerateFiles(legacySave, "*", SearchOption.TopDirectoryOnly))
            {
                var fileName = Path.GetFileName(source);
                if (fileName.Equals("erin-timer.json", StringComparison.OrdinalIgnoreCase) ||
                    fileName.Equals("erin-timer.json.bak", StringComparison.OrdinalIgnoreCase))
                {
                    if (CopyIfMissing(source, Path.Combine(target, fileName)))
                    {
                        profilesCopied++;
                    }
                    continue;
                }

                if ((fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                     fileName.EndsWith(".json.bak", StringComparison.OrdinalIgnoreCase)) &&
                    CopyIfMissing(source, Path.Combine(profilesTarget, fileName)))
                {
                    profilesCopied++;
                }
            }
        }

        var logsCopied = 0;
        var legacyLogs = Path.Combine(legacy, "Logs");
        if (Directory.Exists(legacyLogs))
        {
            var legacyLogsTarget = Path.Combine(logsTarget, "LegacyPortable");
            foreach (var source in Directory.EnumerateFiles(legacyLogs, "*", SearchOption.TopDirectoryOnly))
            {
                if (CopyIfMissing(source, Path.Combine(legacyLogsTarget, Path.GetFileName(source))))
                {
                    logsCopied++;
                }
            }
        }

        File.WriteAllText(
            markerPath,
            $"MigratedFrom={legacy}{Environment.NewLine}MigratedAt={DateTimeOffset.Now:O}{Environment.NewLine}");
        return new PortableDataMigrationResult(true, settingsCopied ? 1 : 0, profilesCopied, logsCopied);
    }

    private static bool CopyIfMissing(string source, string destination)
    {
        if (!File.Exists(source) || File.Exists(destination))
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: false);
        return true;
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(left),
            Path.TrimEndingDirectorySeparator(right),
            StringComparison.OrdinalIgnoreCase);
}

public sealed record PortableDataMigrationResult(
    bool Attempted,
    int SettingsFilesCopied,
    int ProfileFilesCopied,
    int LogFilesCopied);
