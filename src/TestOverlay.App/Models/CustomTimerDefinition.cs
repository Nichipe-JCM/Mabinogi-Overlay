namespace TestOverlay.App.Models;

public sealed class CustomTimerDefinition
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public int DurationSeconds { get; set; } = 60;

    public int AlertBeforeSeconds { get; set; } = 10;

    public string StartHotkey { get; set; } = "Ctrl+Shift+F6";

    public string CancelHotkey { get; set; } = "Ctrl+Shift+F7";

    public bool VisualAlertEnabled { get; set; } = true;

    public bool SoundAlertEnabled { get; set; } = true;

    public string SoundPath { get; set; } = string.Empty;

    public int Volume { get; set; } = 100;

    public CustomTimerDefinition Clone() => new()
    {
        Id = Id,
        Name = Name,
        Enabled = Enabled,
        DurationSeconds = DurationSeconds,
        AlertBeforeSeconds = AlertBeforeSeconds,
        StartHotkey = StartHotkey,
        CancelHotkey = CancelHotkey,
        VisualAlertEnabled = VisualAlertEnabled,
        SoundAlertEnabled = SoundAlertEnabled,
        SoundPath = SoundPath,
        Volume = Volume
    };

    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? $"Timer {Id}" : Name;
}

public sealed record ActiveCustomTimerDisplay(int Id, string Name, int RemainingSeconds, bool IsAlerting);
