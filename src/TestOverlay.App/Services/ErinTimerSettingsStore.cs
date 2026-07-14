using System.IO;
using System.Text.Json;
using TestOverlay.App.Models;

namespace TestOverlay.App.Services;

public sealed class ErinTimerSettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public ErinTimerSettingsStore()
    {
        AppDataPaths.EnsureInitialized();
    }

    public string SettingsPath { get; } = AppDataPaths.ErinTimerSettingsPath;

    public bool LastLoadRecoveredFromBackup { get; private set; }

    public Exception? LastLoadException { get; private set; }

    public ErinTimerSettings Load()
    {
        try
        {
            var result = AtomicJsonFile.Load<ErinTimerSettings>(SettingsPath, Options);
            var settings = result?.Value ?? new ErinTimerSettings();
            Normalize(settings);
            LastLoadRecoveredFromBackup = result?.RecoveredFromBackup == true;
            LastLoadException = result?.PrimaryException;
            return settings;
        }
        catch (Exception exception)
        {
            LastLoadRecoveredFromBackup = false;
            LastLoadException = exception;
            return new ErinTimerSettings();
        }
    }

    public void Save(ErinTimerSettings settings)
    {
        Normalize(settings);
        AtomicJsonFile.Save(SettingsPath, settings, Options);
    }

    private static void Normalize(ErinTimerSettings settings)
    {
        settings.Volume = Math.Clamp(settings.Volume, 0, 1);
        settings.PreMuteVolume = Math.Clamp(settings.PreMuteVolume, 0.01, 1);
        settings.SyncDelaySeconds = Math.Clamp(settings.SyncDelaySeconds, -30, 30);
        settings.AudioFile = ExistingFileOrNull(settings.AudioFile);

        var usedTimes = new HashSet<(int Hour, int Minute)>();
        var usedIds = new HashSet<int>();
        var nextId = 1;
        var normalized = new List<ErinAlarm>();
        foreach (var alarm in settings.Alarms ?? [])
        {
            var hour = ((alarm.Hour % 24) + 24) % 24;
            var minute = Math.Clamp(alarm.Minute / 10 * 10, 0, 50);
            if (!usedTimes.Add((hour, minute)))
            {
                continue;
            }

            var id = alarm.Id > 0 && usedIds.Add(alarm.Id) ? alarm.Id : NextAvailableId(usedIds, ref nextId);
            nextId = Math.Max(nextId, id + 1);
            var customAudioFile = ExistingFileOrNull(alarm.CustomAudioFile);
            alarm.Id = id;
            alarm.Name = (alarm.Name ?? string.Empty).Trim();
            alarm.Hour = hour;
            alarm.Minute = minute;
            alarm.CustomSoundEnabled = alarm.CustomSoundEnabled && customAudioFile is not null;
            alarm.CustomAudioFile = customAudioFile;
            normalized.Add(alarm);
        }

        settings.Alarms = normalized.OrderBy(alarm => alarm.Hour).ThenBy(alarm => alarm.Minute).ToList();
    }

    private static int NextAvailableId(HashSet<int> usedIds, ref int nextId)
    {
        while (!usedIds.Add(nextId))
        {
            nextId++;
        }

        return nextId++;
    }

    private static string? ExistingFileOrNull(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? Path.GetFullPath(path) : null;
}
