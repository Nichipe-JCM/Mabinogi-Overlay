using System.IO;
using System.Text.Json;
using TestOverlay.App.Models;

namespace TestOverlay.App.Services;

public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string ProfileDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TestOverlayProj",
        "Profiles");

    public string DefaultProfilePath => Path.Combine(ProfileDirectory, "default.json");

    public string GetProfilePath(string? profileName) =>
        Path.Combine(ProfileDirectory, $"{NormalizeProfileName(profileName)}.json");

    public string Save(OverlayProfile profile, string? profileName)
    {
        Directory.CreateDirectory(ProfileDirectory);
        var path = GetProfilePath(profileName);
        File.WriteAllText(path, JsonSerializer.Serialize(profile, Options));
        return path;
    }

    public OverlayProfile? Load(string? profileName)
    {
        var path = GetProfilePath(profileName);
        if (!File.Exists(path))
        {
            return null;
        }

        return JsonSerializer.Deserialize<OverlayProfile>(File.ReadAllText(path), Options);
    }

    public OverlayProfile? LoadDefault() => Load("default");

    private static string NormalizeProfileName(string? profileName)
    {
        var name = string.IsNullOrWhiteSpace(profileName) ? "default" : profileName.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return string.IsNullOrWhiteSpace(name) ? "default" : name;
    }
}
