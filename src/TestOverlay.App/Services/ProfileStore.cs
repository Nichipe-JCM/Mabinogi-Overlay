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

    public void Save(OverlayProfile profile)
    {
        Directory.CreateDirectory(ProfileDirectory);
        File.WriteAllText(DefaultProfilePath, JsonSerializer.Serialize(profile, Options));
    }

    public OverlayProfile? LoadDefault()
    {
        if (!File.Exists(DefaultProfilePath))
        {
            return null;
        }

        return JsonSerializer.Deserialize<OverlayProfile>(File.ReadAllText(DefaultProfilePath), Options);
    }
}
