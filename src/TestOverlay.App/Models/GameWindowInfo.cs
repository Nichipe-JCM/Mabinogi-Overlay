namespace TestOverlay.App.Models;

public sealed record GameWindowInfo(
    nint Handle,
    string Title,
    string ProcessName,
    int ClientWidth,
    int ClientHeight)
{
    public bool LooksLikeMabinogi =>
        Title.Contains("Mabinogi", StringComparison.OrdinalIgnoreCase) ||
        Title.Contains("마비노기", StringComparison.OrdinalIgnoreCase) ||
        ProcessName.Contains("mabinogi", StringComparison.OrdinalIgnoreCase);

    public string DisplayName =>
        $"{Title} ({ProcessName}) - {ClientWidth}x{ClientHeight}";
}
