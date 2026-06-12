namespace TestOverlay.App.Models;

public sealed record GameWindowInfo(
    nint Handle,
    string Title,
    string ProcessName,
    string ProcessExecutableName,
    int ClientWidth,
    int ClientHeight)
{
    private const string MabinogiKoreanTitle = "\uB9C8\uBE44\uB178\uAE30";
    private const string PreferredMabinogiClientTitle = "\uB9C8\uBE44\uB178\uAE30 (Client)";

    public bool LooksLikeMabinogi =>
        Title.Contains("Mabinogi", StringComparison.OrdinalIgnoreCase) ||
        Title.Contains(MabinogiKoreanTitle, StringComparison.OrdinalIgnoreCase) ||
        ProcessName.Contains("mabinogi", StringComparison.OrdinalIgnoreCase) ||
        IsExactClientExecutable ||
        IsPreferredMabinogiClient;

    public bool IsExactClientExecutable =>
        ProcessExecutableName.Equals("Client.exe", StringComparison.OrdinalIgnoreCase);

    public bool IsPreferredMabinogiClient =>
        Title.Contains(PreferredMabinogiClientTitle, StringComparison.OrdinalIgnoreCase) &&
        IsExactClientExecutable;

    public string DisplayName =>
        $"{Title} ({ProcessExecutableName}) - {ClientWidth}x{ClientHeight}";
}
