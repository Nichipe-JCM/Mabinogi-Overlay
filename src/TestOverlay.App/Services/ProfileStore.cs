using System.IO;
using System.IO.Compression;
using System.Text.Json;
using TestOverlay.App.Models;

namespace TestOverlay.App.Services;

public sealed class ProfileStore
{
    public const string ProfilePackageExtension = ".moverlayprofile";
    private const string PackageProfileEntryName = "profile.json";
    private const long MaximumProfileJsonBytes = 10 * 1024 * 1024;
    private const long MaximumPackagedAudioBytes = AudioFilePolicy.MaximumAudioBytes;
    private const long MaximumPackageAudioBytes = 200 * 1024 * 1024;
    private const int MaximumPackageEntries = 128;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public ProfileStore(string profileDirectory)
    {
        ProfileDirectory = profileDirectory;
    }

    public string ProfileDirectory { get; private set; }

    public string DefaultProfilePath => Path.Combine(ProfileDirectory, "default.json");

    public bool LastLoadRecoveredFromBackup { get; private set; }

    public Exception? LastRestoreException { get; private set; }

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
        LastRestoreException = null;
        var path = GetProfilePath(profileName);
        var result = AtomicJsonFile.Load<OverlayProfile>(path, Options, OverlayProfileValidator.Validate);
        if (result is null)
        {
            LastLoadRecoveredFromBackup = false;
            return null;
        }

        LastLoadRecoveredFromBackup = result.RecoveredFromBackup;
        LastRestoreException = result.RestoreException;
        return result.Value;
    }

    public OverlayProfile? LoadDefault() => Load("default");

    public string Rename(string? currentName, string? newName)
    {
        var sourceName = NormalizeProfileName(currentName);
        var targetName = NormalizeProfileName(newName);
        var profile = Load(sourceName)
            ?? throw new FileNotFoundException("The profile to rename does not exist.", GetProfilePath(sourceName));
        if (string.Equals(sourceName, targetName, StringComparison.OrdinalIgnoreCase))
        {
            profile.Name = targetName;
            Save(profile, targetName);
            return targetName;
        }

        if (Exists(targetName))
        {
            throw new IOException($"A profile named '{targetName}' already exists.");
        }

        var sourcePath = GetProfilePath(sourceName);
        var sourceBackupPath = AtomicJsonFile.GetBackupPath(sourcePath);
        var targetPath = GetProfilePath(targetName);
        var targetBackupPath = AtomicJsonFile.GetBackupPath(targetPath);
        File.Move(sourcePath, targetPath);
        try
        {
            profile.Name = targetName;
            Save(profile, targetName);
            File.Delete(sourceBackupPath);
            if (Exists(sourceName))
            {
                throw new IOException($"The original profile '{sourceName}' still exists after renaming.");
            }
        }
        catch
        {
            if (!File.Exists(sourcePath))
            {
                if (File.Exists(targetPath))
                {
                    File.Move(targetPath, sourcePath);
                }
                else if (File.Exists(targetBackupPath))
                {
                    File.Move(targetBackupPath, sourcePath);
                }
            }
            File.Delete(targetBackupPath);
            throw;
        }

        return targetName;
    }

    public string Import(string sourcePath, string? profileName = null)
    {
        var importedName = NormalizeProfileName(string.IsNullOrWhiteSpace(profileName)
            ? Path.GetFileNameWithoutExtension(sourcePath)
            : profileName);
        var packageImport = IsZipPackage(sourcePath);
        OverlayProfile profile;
        if (packageImport)
        {
            profile = ImportPackage(sourcePath, importedName);
        }
        else
        {
            var info = new FileInfo(sourcePath);
            if (info.Length > MaximumProfileJsonBytes)
            {
                throw new InvalidDataException("The profile data is too large.");
            }
            profile = DeserializeProfile(File.ReadAllText(sourcePath));
        }
        var saved = false;
        try
        {
            OverlayProfileValidator.Validate(profile);
            profile.Name = importedName;
            var previousProfile = Load(importedName);
            Save(profile, importedName);
            saved = true;
            if (previousProfile is not null)
            {
                DeleteOwnedAssetDirectories(previousProfile, profile);
                // Replace the atomic backup as well so recovery cannot restore paths to
                // package assets that were just retired.
                Save(profile, importedName);
            }
            return importedName;
        }
        catch
        {
            if (packageImport && !saved)
            {
                DeleteOwnedAssetDirectories(profile);
            }
            throw;
        }
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
        ExportPackage(profile, destinationPath);
    }

    public void Delete(string? profileName)
    {
        var normalizedName = NormalizeProfileName(profileName);
        var path = GetProfilePath(normalizedName);
        var backupPath = AtomicJsonFile.GetBackupPath(path);
        if (!File.Exists(path) && !File.Exists(backupPath))
        {
            throw new FileNotFoundException("The profile to delete does not exist.", path);
        }

        var profile = Load(normalizedName);
        File.Delete(backupPath);
        File.Delete(path);
        if (File.Exists(path) || File.Exists(backupPath))
        {
            throw new IOException($"The profile '{normalizedName}' could not be deleted completely.");
        }

        if (profile is not null)
        {
            DeleteOwnedAssetDirectories(profile);
        }
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

        name = name.TrimEnd(' ', '.');
        if (name.Length > 80)
        {
            name = name[..80].TrimEnd(' ', '.');
        }
        if (IsReservedWindowsName(name))
        {
            name = $"_{name}";
        }

        return string.IsNullOrWhiteSpace(name) ? "default" : name;
    }

    public static void ValidateUserProfileName(string? profileName)
    {
        var name = profileName?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            throw new InvalidDataException("Enter a profile name.");
        }
        if (name.Length > 80)
        {
            throw new InvalidDataException("Profile names can contain at most 80 characters.");
        }
        if (name.EndsWith(' ') || name.EndsWith('.') ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            IsReservedWindowsName(name))
        {
            throw new InvalidDataException("The profile name is not valid on Windows.");
        }
    }

    private static bool IsReservedWindowsName(string name)
    {
        var stem = name.Split('.')[0];
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
               (stem.Length == 4 &&
                (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                 stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                stem[3] is >= '1' and <= '9');
    }

    private void ExportPackage(OverlayProfile profile, string destinationPath)
    {
        var portableProfile = DeserializeProfile(JsonSerializer.Serialize(profile, Options));
        var packagedPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var audioIndex = 0;
        long totalAudioBytes = 0;
        var temporaryPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp";

        try
        {
            using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            {
                string PackageAudio(string path, string label)
                {
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        return string.Empty;
                    }
                    if (!File.Exists(path))
                    {
                        // Keep package export usable when an optional custom
                        // sound was removed after the profile was saved.
                        return string.Empty;
                    }
                    var validatedPath = AudioFilePolicy.Validate(path);
                    if (packagedPaths.TryGetValue(validatedPath, out var existing))
                    {
                        return existing;
                    }

                    var info = new FileInfo(validatedPath);
                    if (info.Length > MaximumPackagedAudioBytes ||
                        totalAudioBytes + info.Length > MaximumPackageAudioBytes)
                    {
                        throw new InvalidDataException("Profile audio files exceed the supported package size.");
                    }

                    totalAudioBytes += info.Length;
                    var safeLabel = string.Concat(label.Select(character =>
                        char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_'));
                    var entryName = $"audio/{++audioIndex:D2}-{safeLabel}{Path.GetExtension(validatedPath)}";
                    var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                    using var source = File.OpenRead(validatedPath);
                    using var destination = entry.Open();
                    CopyWithLimit(source, destination, MaximumPackagedAudioBytes);
                    packagedPaths[validatedPath] = entryName;
                    return entryName;
                }

                portableProfile.BuffAlertSoundPath =
                    PackageAudio(portableProfile.BuffAlertSoundPath, "buff-global");
                foreach (var key in portableProfile.BuffAlertSoundPaths.Keys.ToList())
                {
                    portableProfile.BuffAlertSoundPaths[key] =
                        PackageAudio(portableProfile.BuffAlertSoundPaths[key], $"buff-{key}");
                }
                portableProfile.TuairimAlertSoundPath =
                    PackageAudio(portableProfile.TuairimAlertSoundPath, "tuairim");
                foreach (var timer in portableProfile.CustomTimers)
                {
                    timer.SoundPath = PackageAudio(timer.SoundPath, $"timer-{timer.Id}");
                }

                var profileEntry = archive.CreateEntry(PackageProfileEntryName, CompressionLevel.Optimal);
                using var writer = new StreamWriter(profileEntry.Open());
                writer.Write(JsonSerializer.Serialize(portableProfile, Options));
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private OverlayProfile ImportPackage(string sourcePath, string importedName)
    {
        using var archive = ZipFile.OpenRead(sourcePath);
        if (archive.Entries.Count > MaximumPackageEntries)
        {
            throw new InvalidDataException("The profile package contains too many entries.");
        }
        var profileEntry = archive.GetEntry(PackageProfileEntryName)
            ?? throw new InvalidDataException("The profile package does not contain profile.json.");
        if (profileEntry.Length > MaximumProfileJsonBytes)
        {
            throw new InvalidDataException("The packaged profile data is too large.");
        }

        OverlayProfile profile;
        using (var reader = new StreamReader(profileEntry.Open()))
        {
            profile = DeserializeProfile(reader.ReadToEnd());
        }

        var assetDirectory = Path.Combine(ProfileDirectory, "Assets", Guid.NewGuid().ToString("N"));
        var extractedPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var extractedAny = false;
        long totalAudioBytes = 0;

        string ExtractAudio(string path)
        {
            if (string.IsNullOrWhiteSpace(path) ||
                !path.Replace('\\', '/').StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }

            var normalizedEntryName = path.Replace('\\', '/');
            if (!string.Equals(
                    normalizedEntryName,
                    $"audio/{Path.GetFileName(normalizedEntryName)}",
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException($"The profile package contains an invalid audio path: '{path}'.");
            }
            if (!AudioFilePolicy.IsSupportedExtension(normalizedEntryName))
            {
                throw new InvalidDataException(
                    $"The profile package contains an unsupported audio format: '{path}'.");
            }
            if (extractedPaths.TryGetValue(normalizedEntryName, out var existingPath))
            {
                return existingPath;
            }
            var entry = archive.GetEntry(normalizedEntryName)
                ?? throw new InvalidDataException($"The profile package is missing '{normalizedEntryName}'.");
            if (entry.Length > MaximumPackagedAudioBytes ||
                totalAudioBytes + entry.Length > MaximumPackageAudioBytes)
            {
                throw new InvalidDataException("Profile audio files exceed the supported package size.");
            }

            Directory.CreateDirectory(assetDirectory);
            var destinationPath = Path.Combine(assetDirectory, Path.GetFileName(normalizedEntryName));
            long copied;
            using (var source = entry.Open())
            using (var destination = new FileStream(
                       destinationPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                copied = CopyWithLimit(
                    source,
                    destination,
                    Math.Min(MaximumPackagedAudioBytes, MaximumPackageAudioBytes - totalAudioBytes));
            }
            AudioFilePolicy.Validate(destinationPath);
            totalAudioBytes += copied;
            extractedAny = true;
            extractedPaths[normalizedEntryName] = destinationPath;
            return destinationPath;
        }

        try
        {
            profile.BuffAlertSoundPath = ExtractAudio(profile.BuffAlertSoundPath);
            foreach (var key in profile.BuffAlertSoundPaths.Keys.ToList())
            {
                profile.BuffAlertSoundPaths[key] = ExtractAudio(profile.BuffAlertSoundPaths[key]);
            }
            profile.TuairimAlertSoundPath = ExtractAudio(profile.TuairimAlertSoundPath);
            foreach (var timer in profile.CustomTimers)
            {
                timer.SoundPath = ExtractAudio(timer.SoundPath);
            }

            profile.Name = importedName;
            return profile;
        }
        catch
        {
            if (Directory.Exists(assetDirectory))
            {
                Directory.Delete(assetDirectory, recursive: true);
            }
            throw;
        }
        finally
        {
            if (!extractedAny && Directory.Exists(assetDirectory))
            {
                Directory.Delete(assetDirectory, recursive: true);
            }
        }
    }

    private void DeleteOwnedAssetDirectories(OverlayProfile profile, OverlayProfile? retainedProfile = null)
    {
        var assetsRoot = Path.GetFullPath(Path.Combine(ProfileDirectory, "Assets")) +
                         Path.DirectorySeparatorChar;
        var retainedDirectories = retainedProfile is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : GetAudioPaths(retainedProfile)
                .Select(Path.GetDirectoryName)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => Path.GetFullPath(path!))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in GetAudioPaths(profile)
                     .Select(Path.GetDirectoryName)
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Select(path => Path.GetFullPath(path!))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var ownedDirectory = directory + Path.DirectorySeparatorChar;
            if (ownedDirectory.StartsWith(assetsRoot, StringComparison.OrdinalIgnoreCase) &&
                !retainedDirectories.Contains(directory) &&
                Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static IEnumerable<string> GetAudioPaths(OverlayProfile profile)
    {
        yield return profile.BuffAlertSoundPath;
        foreach (var path in profile.BuffAlertSoundPaths.Values)
        {
            yield return path;
        }
        yield return profile.TuairimAlertSoundPath;
        foreach (var timer in profile.CustomTimers)
        {
            yield return timer.SoundPath;
        }
    }

    private static OverlayProfile DeserializeProfile(string json) =>
        JsonSerializer.Deserialize<OverlayProfile>(json, Options)
        ?? throw new InvalidDataException("The selected file does not contain a profile.");

    private static bool IsZipPackage(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> signature = stackalloc byte[4];
        return stream.Read(signature) == signature.Length &&
               signature[0] == (byte)'P' &&
               signature[1] == (byte)'K';
    }

    private static long CopyWithLimit(Stream source, Stream destination, long maximumBytes)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > maximumBytes)
            {
                throw new InvalidDataException("Profile audio files exceed the supported package size.");
            }
            destination.Write(buffer, 0, read);
        }
        return total;
    }

}
