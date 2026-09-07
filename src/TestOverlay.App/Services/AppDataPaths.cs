using System.IO;

namespace TestOverlay.App.Services;

public static class AppDataPaths
{
    private static readonly object MigrationSync = new();
    private static bool _migrationAttempted;

    public static string RootDirectory { get; } = TestLabEnvironment.Root ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Mabinogi Overlay");

    public static string SettingsPath => Path.Combine(RootDirectory, "settings.json");
    public static string ProfilesDirectory => Path.Combine(RootDirectory, "Profiles");
    public static string LogDirectory => Path.Combine(RootDirectory, "Logs");
    public static string ErinTimerSettingsPath => Path.Combine(RootDirectory, "erin-timer.json");
    public static string LegacyRootDirectory => AppContext.BaseDirectory;
    public static string LegacyProfilesDirectory => Path.Combine(LegacyRootDirectory, "save");

    public static Exception? LastMigrationException { get; private set; }

    public static void EnsureInitialized()
    {
        lock (MigrationSync)
        {
            if (_migrationAttempted)
            {
                return;
            }

            _migrationAttempted = true;
            try
            {
                if (TestLabEnvironment.Enabled)
                {
                    Directory.CreateDirectory(RootDirectory);
                    Directory.CreateDirectory(ProfilesDirectory);
                    Directory.CreateDirectory(LogDirectory);
                }
                else PortableDataMigration.Migrate(LegacyRootDirectory, RootDirectory);
            }
            catch (Exception exception)
            {
                LastMigrationException = exception;
                Directory.CreateDirectory(RootDirectory);
                Directory.CreateDirectory(ProfilesDirectory);
                Directory.CreateDirectory(LogDirectory);
            }
        }
    }
}
