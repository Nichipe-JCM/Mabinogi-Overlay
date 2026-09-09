namespace TestOverlay.App.Models;

public sealed record ThemeSettings
{
    public string Mode { get; set; } = "Default";
    public string Background { get; set; } = "#111315";
    public string Accent { get; set; } = "#89DED4";
    public string Foreground { get; set; } = "#ECECEC";
}
