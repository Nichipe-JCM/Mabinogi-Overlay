namespace TestOverlay.App.Models;

public sealed class ErinTimerSettings
{
    public int ConfigVersion { get; set; } = 1;

    public string? AudioFile { get; set; }

    public double Volume { get; set; } = 0.5;

    public double PreMuteVolume { get; set; } = 0.5;

    public bool IsMuted { get; set; }

    public bool DesktopNotifications { get; set; } = true;

    public double SyncDelaySeconds { get; set; } = 6.5;

    public List<ErinAlarm> Alarms { get; set; } = [];
}

public sealed class ErinAlarm
{
    public int Id { get; set; }

    public int Hour { get; set; }

    public int Minute { get; set; }

    public bool Repeat { get; set; }

    public bool Enabled { get; set; }

    public bool CustomSoundEnabled { get; set; }

    public string? CustomAudioFile { get; set; }
}
