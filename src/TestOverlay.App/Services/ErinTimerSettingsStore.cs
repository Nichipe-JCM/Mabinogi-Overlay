using System.IO;
using System.Text.Json;
using TestOverlay.App.Models;

namespace TestOverlay.App.Services;

public sealed class ErinTimerSettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string SettingsPath { get; } = Path.Combine(AppContext.BaseDirectory, "save", "erin-timer.json");

    public ErinTimerSettings Load()
    {
        if (!File.Exists(SettingsPath))
        {
            return new ErinTimerSettings();
        }

        try
        {
            var settings = JsonSerializer.Deserialize<ErinTimerSettings>(File.ReadAllText(SettingsPath), Options)
                           ?? new ErinTimerSettings();
            Normalize(settings);
            return settings;
        }
        catch
        {
            return new ErinTimerSettings();
        }
    }

    public void Save(ErinTimerSettings settings)
    {
        Normalize(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath) ?? AppContext.BaseDirectory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, Options));
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
            normalized.Add(new ErinAlarm
            {
                Id = id,
                Hour = hour,
                Minute = minute,
                Repeat = alarm.Repeat,
                Enabled = alarm.Enabled,
                CustomSoundEnabled = alarm.CustomSoundEnabled && customAudioFile is not null,
                CustomAudioFile = customAudioFile
            });
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
