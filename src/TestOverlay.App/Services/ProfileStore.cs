using System.IO;
using System.Text.Json;
using TestOverlay.App.Models;

namespace TestOverlay.App.Services;

public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public ProfileStore(string profileDirectory)
    {
        ProfileDirectory = profileDirectory;
    }

    public string ProfileDirectory { get; private set; }

    public string DefaultProfilePath => Path.Combine(ProfileDirectory, "default.json");

    public bool LastLoadRecoveredFromBackup { get; private set; }

    public void SetProfileDirectory(string profileDirectory)
    {
        ProfileDirectory = profileDirectory;
    }

    public string GetProfilePath(string? profileName) =>
        Path.Combine(ProfileDirectory, $"{NormalizeProfileName(profileName)}.json");

    public bool Exists(string? profileName) => AtomicJsonFile.Exists(GetProfilePath(profileName));

    public string Save(OverlayProfile profile, string? profileName)
    {
        OverlayProfileValidator.Validate(profile);
        var path = GetProfilePath(profileName);
        AtomicJsonFile.Save(path, profile, Options);
        return path;
    }

    public OverlayProfile? Load(string? profileName)
    {
        LastLoadRecoveredFromBackup = false;
        var path = GetProfilePath(profileName);
        var result = AtomicJsonFile.Load<OverlayProfile>(path, Options, OverlayProfileValidator.Validate);
        if (result is null)
        {
            LastLoadRecoveredFromBackup = false;
            return null;
        }

        LastLoadRecoveredFromBackup = result.RecoveredFromBackup;
        return result.Value;
    }

    public OverlayProfile? LoadDefault() => Load("default");

    public string Rename(string? currentName, string? newName)
    {
        var sourceName = NormalizeProfileName(currentName);
        var targetName = NormalizeProfileName(newName);
        var profile = Load(sourceName)
            ?? throw new FileNotFoundException("The profile to rename does not exist.", GetProfilePath(sourceName));
        if (!string.Equals(sourceName, targetName, StringComparison.OrdinalIgnoreCase) && Exists(targetName))
        {
            throw new IOException($"A profile named '{targetName}' already exists.");
        }

        profile.Name = targetName;
        Save(profile, targetName);
        if (!string.Equals(sourceName, targetName, StringComparison.OrdinalIgnoreCase))
        {
            DeleteProfileFiles(sourceName);
        }

        return targetName;
    }

    public string Import(string sourcePath, string? profileName = null)
    {
        var json = File.ReadAllText(sourcePath);
        var profile = JsonSerializer.Deserialize<OverlayProfile>(json, Options)
            ?? throw new InvalidDataException("The selected file does not contain a profile.");
        OverlayProfileValidator.Validate(profile);
        var importedName = NormalizeProfileName(string.IsNullOrWhiteSpace(profileName)
            ? Path.GetFileNameWithoutExtension(sourcePath)
            : profileName);
        profile.Name = importedName;
        Save(profile, importedName);
        return importedName;
    }

    public void Export(string? profileName, string destinationPath)
    {
        var normalizedName = NormalizeProfileName(profileName);
        var profile = Load(normalizedName)
            ?? throw new FileNotFoundException("The profile to export does not exist.", GetProfilePath(normalizedName));
        profile.Name = normalizedName;
        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
        File.WriteAllText(destinationPath, JsonSerializer.Serialize(profile, Options));
    }

    public IReadOnlyList<string> ListProfileNames()
    {
        if (!Directory.Exists(ProfileDirectory))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(ProfileDirectory, "*", SearchOption.TopDirectoryOnly)
            .Select(GetProfileNameFromDataPath)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToList();
    }

    private static string? GetProfileNameFromDataPath(string path)
    {
        var fileName = Path.GetFileName(path);
        if (fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return fileName[..^".json".Length];
        }
        if (fileName.EndsWith(".json.bak", StringComparison.OrdinalIgnoreCase))
        {
            return fileName[..^".json.bak".Length];
        }

        return null;
    }

    public static string NormalizeProfileName(string? profileName)
    {
        var name = string.IsNullOrWhiteSpace(profileName) ? "default" : profileName.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return string.IsNullOrWhiteSpace(name) ? "default" : name;
    }

    private void DeleteProfileFiles(string profileName)
    {
        var path = GetProfilePath(profileName);
        File.Delete(path);
        File.Delete($"{path}.bak");
    }
}
