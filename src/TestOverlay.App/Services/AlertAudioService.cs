using System.IO;
using System.Windows.Media;
using TestOverlay.App.Models;

namespace TestOverlay.App.Services;

public sealed class AlertAudioService : IDisposable
{
    private const int DefaultSoundSampleRate = 44100;
    private readonly AppLog _log;
    private readonly IReadOnlyList<string> _buffNameKeys;
    private readonly MediaPlayer _globalBuffPlayer = new();
    private readonly Dictionary<string, MediaPlayer> _individualBuffPlayers;
    private readonly MediaPlayer _tuairimPlayer = new();
    private string? _defaultAlertSoundPath;
    private bool _isDisposed;

    public AlertAudioService(AppLog log, IReadOnlyList<string> buffNameKeys)
    {
        _log = log;
        _buffNameKeys = buffNameKeys;
        _individualBuffPlayers = buffNameKeys.ToDictionary(
            key => key,
            key => CreatePlayer($"Buff alert media playback failed: key={key}."),
            StringComparer.Ordinal);
        _globalBuffPlayer.MediaFailed += (_, args) =>
            _log.Error("Buff alert media playback failed.", args.ErrorException);
        _tuairimPlayer.MediaFailed += (_, args) =>
            _log.Error("Tuairim alert media playback failed.", args.ErrorException);
    }

    public MonitorAlertSettings Settings { get; } = new();

    public string GetBuffPath(int index)
    {
        if (Settings.BuffSoundMode == MonitorAlertSettings.GlobalMode)
        {
            return index == 0 ? Settings.GlobalBuffPath : string.Empty;
        }

        return Settings.BuffPaths.GetValueOrDefault(_buffNameKeys[index], string.Empty);
    }

    public void SetBuffPath(int index, string path)
    {
        if (Settings.BuffSoundMode == MonitorAlertSettings.GlobalMode)
        {
            if (index == 0)
            {
                Settings.GlobalBuffPath = path;
            }

            return;
        }

        Settings.BuffPaths[_buffNameKeys[index]] = path;
    }

    public int GetBuffVolume(int index)
    {
        if (Settings.BuffSoundMode == MonitorAlertSettings.GlobalMode)
        {
            return index == 0 ? Settings.GlobalBuffVolume : 100;
        }

        return Settings.BuffVolumes.GetValueOrDefault(_buffNameKeys[index], 100);
    }

    public void SetBuffVolume(int index, int volume)
    {
        volume = Math.Clamp(volume, 0, 100);
        if (Settings.BuffSoundMode == MonitorAlertSettings.GlobalMode)
        {
            if (index == 0)
            {
                Settings.GlobalBuffVolume = volume;
            }

            return;
        }

        Settings.BuffVolumes[_buffNameKeys[index]] = volume;
    }

    public void ClearBuff(int index)
    {
        SetBuffPath(index, string.Empty);
        StopAndClose(ResolveBuffPlayer(_buffNameKeys[index]));
    }

    public void ClearTuairim()
    {
        Settings.TuairimPath = string.Empty;
        StopAndClose(_tuairimPlayer);
    }

    public void TestBuff(int index) =>
        Play(
            ResolveBuffPlayer(_buffNameKeys[index]),
            GetBuffPath(index),
            GetBuffVolume(index),
            $"buff-test:{index}");

    public void TestTuairim() =>
        Play(_tuairimPlayer, Settings.TuairimPath, Settings.TuairimVolume, "tuairim-test");

    public void PlayBuff(string nameKey, string alertKind) =>
        Play(
            ResolveBuffPlayer(nameKey),
            Settings.BuffSoundMode == MonitorAlertSettings.IndividualMode
                ? Settings.BuffPaths.GetValueOrDefault(nameKey, string.Empty)
                : Settings.GlobalBuffPath,
            Settings.BuffSoundMode == MonitorAlertSettings.IndividualMode
                ? Settings.BuffVolumes.GetValueOrDefault(nameKey, 100)
                : Settings.GlobalBuffVolume,
            alertKind);

    public void PlayTuairim(string alertKind) =>
        Play(_tuairimPlayer, Settings.TuairimPath, Settings.TuairimVolume, alertKind);

    public void LoadProfile(OverlayProfile profile)
    {
        Settings.BuffAlertSeconds = profile.BuffAlertSeconds is >= 5 and <= 30
            ? profile.BuffAlertSeconds
            : 30;
        Settings.GlobalBuffPath = profile.BuffAlertSoundPath ?? string.Empty;
        Settings.GlobalBuffVolume = profile.BuffAlertVolume is >= 0 and <= 100
            ? profile.BuffAlertVolume
            : 100;
        Settings.BuffSoundMode = profile.BuffAlertSoundMode == MonitorAlertSettings.IndividualMode
            ? MonitorAlertSettings.IndividualMode
            : MonitorAlertSettings.GlobalMode;
        Settings.BuffPaths.Clear();
        Settings.BuffVolumes.Clear();
        foreach (var nameKey in _buffNameKeys)
        {
            if (profile.BuffAlertSoundPaths?.TryGetValue(nameKey, out var path) == true)
            {
                Settings.BuffPaths[nameKey] = path ?? string.Empty;
            }

            if (profile.BuffAlertVolumes?.TryGetValue(nameKey, out var volume) == true)
            {
                Settings.BuffVolumes[nameKey] = Math.Clamp(volume, 0, 100);
            }
        }

        Settings.TuairimAlertPercent = profile.TuairimAlertPercent is >= 90 and <= 100
            ? profile.TuairimAlertPercent
            : 95;
        Settings.TuairimPath = profile.TuairimAlertSoundPath ?? string.Empty;
        Settings.TuairimVolume = profile.TuairimAlertVolume is >= 0 and <= 100
            ? profile.TuairimAlertVolume
            : 100;
        Settings.TuairimFrequency = profile.TuairimAlertFrequency == MonitorAlertSettings.EveryPercentFrequency
            ? MonitorAlertSettings.EveryPercentFrequency
            : MonitorAlertSettings.OnceFrequency;
    }

    public void WriteProfile(OverlayProfile profile)
    {
        profile.BuffAlertSeconds = Settings.BuffAlertSeconds;
        profile.BuffAlertSoundPath = Settings.GlobalBuffPath;
        profile.BuffAlertVolume = Settings.GlobalBuffVolume;
        profile.BuffAlertSoundMode = Settings.BuffSoundMode;
        profile.BuffAlertSoundPaths = new Dictionary<string, string>(Settings.BuffPaths, StringComparer.Ordinal);
        profile.BuffAlertVolumes = new Dictionary<string, int>(Settings.BuffVolumes, StringComparer.Ordinal);
        profile.TuairimAlertPercent = Settings.TuairimAlertPercent;
        profile.TuairimAlertSoundPath = Settings.TuairimPath;
        profile.TuairimAlertVolume = Settings.TuairimVolume;
        profile.TuairimAlertFrequency = Settings.TuairimFrequency;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        StopAndClose(_globalBuffPlayer);
        foreach (var player in _individualBuffPlayers.Values)
        {
            StopAndClose(player);
        }

        StopAndClose(_tuairimPlayer);
    }

    private MediaPlayer ResolveBuffPlayer(string nameKey) =>
        Settings.BuffSoundMode == MonitorAlertSettings.IndividualMode
            ? _individualBuffPlayers[nameKey]
            : _globalBuffPlayer;

    private void Play(MediaPlayer player, string path, int volume, string alertKind)
    {
        var usesDefaultSound = string.IsNullOrWhiteSpace(path);
        var resolvedPath = usesDefaultSound ? EnsureDefaultAlertSoundPath() : path;
        if (!usesDefaultSound && !File.Exists(resolvedPath))
        {
            _log.Info($"Monitor alert custom sound unavailable: kind={alertKind}, path={path}");
            return;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(resolvedPath) || !File.Exists(resolvedPath))
            {
                System.Media.SystemSounds.Asterisk.Play();
                _log.Info($"Monitor default system alert played: kind={alertKind}, volume=system");
                return;
            }

            StopAndClose(player);
            player.Open(new Uri(resolvedPath, UriKind.Absolute));
            player.Volume = Math.Clamp(volume, 0, 100) / 100.0;
            player.Play();
            _log.Info(
                $"Monitor alert sound played: kind={alertKind}, volume={volume}, " +
                $"source={(usesDefaultSound ? "default" : "custom")}, path={resolvedPath}");
        }
        catch (Exception exception)
        {
            _log.Error($"Failed to play monitor alert sound: kind={alertKind}, path={resolvedPath}", exception);
        }
    }

    private string? EnsureDefaultAlertSoundPath()
    {
        if (!string.IsNullOrWhiteSpace(_defaultAlertSoundPath) && File.Exists(_defaultAlertSoundPath))
        {
            return _defaultAlertSoundPath;
        }

        try
        {
            var directory = Path.Combine(Path.GetTempPath(), "MabinogiOverlay");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "default-alert-v1.wav");
            if (!File.Exists(path) || new FileInfo(path).Length < 128)
            {
                WriteDefaultAlertSound(path);
            }

            _defaultAlertSoundPath = path;
            return path;
        }
        catch (Exception exception)
        {
            _log.Error("Failed to prepare the built-in monitor alert sound.", exception);
            return null;
        }
    }

    private static void WriteDefaultAlertSound(string path)
    {
        const double durationSeconds = 0.36;
        const short channelCount = 1;
        const short bitsPerSample = 16;
        var sampleCount = (int)(DefaultSoundSampleRate * durationSeconds);
        var dataSize = sampleCount * channelCount * (bitsPerSample / 8);

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + dataSize);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channelCount);
        writer.Write(DefaultSoundSampleRate);
        writer.Write(DefaultSoundSampleRate * channelCount * (bitsPerSample / 8));
        writer.Write((short)(channelCount * (bitsPerSample / 8)));
        writer.Write(bitsPerSample);
        writer.Write("data"u8.ToArray());
        writer.Write(dataSize);

        for (var index = 0; index < sampleCount; index++)
        {
            var time = index / (double)DefaultSoundSampleRate;
            var noteTime = time % 0.18;
            var frequency = time < 0.18 ? 783.99 : 1046.50;
            var attack = Math.Min(1, noteTime / 0.012);
            var release = Math.Min(1, Math.Max(0, (0.18 - noteTime) / 0.055));
            var envelope = attack * release;
            var sample = Math.Sin(2 * Math.PI * frequency * time) * envelope * 0.28;
            writer.Write((short)Math.Round(sample * short.MaxValue));
        }
    }

    private MediaPlayer CreatePlayer(string errorMessage)
    {
        var player = new MediaPlayer();
        player.MediaFailed += (_, args) => _log.Error(errorMessage, args.ErrorException);
        return player;
    }

    private static void StopAndClose(MediaPlayer player)
    {
        player.Stop();
        player.Close();
    }
}

public sealed class MonitorAlertSettings
{
    public const string GlobalMode = "global";
    public const string IndividualMode = "individual";
    public const string OnceFrequency = "once";
    public const string EveryPercentFrequency = "every-percent";

    public int BuffAlertSeconds { get; set; } = 30;
    public string GlobalBuffPath { get; set; } = string.Empty;
    public int GlobalBuffVolume { get; set; } = 100;
    public string BuffSoundMode { get; set; } = GlobalMode;
    public Dictionary<string, string> BuffPaths { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> BuffVolumes { get; } = new(StringComparer.Ordinal);
    public int TuairimAlertPercent { get; set; } = 95;
    public string TuairimPath { get; set; } = string.Empty;
    public int TuairimVolume { get; set; } = 100;
    public string TuairimFrequency { get; set; } = OnceFrequency;
}
