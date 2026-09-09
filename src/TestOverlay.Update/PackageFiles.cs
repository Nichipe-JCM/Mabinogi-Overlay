using System.IO.Compression;

namespace TestOverlay.Update;

public static class PackageFiles
{
    public static void Validate(string[] files)
    {
        if (files.Length is < 2 or > 2000) throw new InvalidDataException("Invalid file list size.");
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            ValidateName(file);
            if (!unique.Add(file)) throw new InvalidDataException("Duplicate file path.");
        }
        if (!unique.Contains(GitHubUpdateClient.MainExecutable) || !unique.Contains("Updater/" + GitHubUpdateClient.HelperExecutable)) throw new InvalidDataException("Required executables missing from file list.");
    }
    public static void ValidateName(string file)
    {
        if (string.IsNullOrWhiteSpace(file) || file.Length > 240 || file.Contains('\\') || file.Contains(':') || Path.IsPathRooted(file)) throw new InvalidDataException("Invalid package path.");
        foreach (var part in file.Split('/'))
        {
            var basename = part.Split('.')[0];
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(basename, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("Unsafe package path.");
        }
        // User settings, personal audio and test tooling can never be update-owned files.
        var top = file.Split('/')[0];
        if (new[] { "settings.json", "erin-timer.json", "Profiles", "save", "Logs", "tests", "src", "tools" }.Contains(top, StringComparer.OrdinalIgnoreCase) ||
            file.StartsWith("docs/updates/", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("User data or development file in product list.");
    }
    public static string Resolve(string root, string relative)
    {
        ValidateName(relative);
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Path escapes installation.");
        RejectLinks(full);
        return full;
    }
    public static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Update paths cannot contain symbolic links or junctions.");
    }
    public static void Extract(string zip, string destination, string[] expected)
    {
        Validate(expected);
        var remaining = new HashSet<string>(expected, StringComparer.OrdinalIgnoreCase);
        using var archive = ZipFile.OpenRead(zip);
        if (archive.Entries.Count > 4000) throw new InvalidDataException("Too many archive entries.");
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/')) { ValidateName(entry.FullName.TrimEnd('/')); continue; }
            // Never normalize backslashes/traversal/duplicates into a permitted file.
            if (!remaining.Remove(entry.FullName)) throw new InvalidDataException("Package does not match published file list.");
            if ((entry.ExternalAttributes >> 16 & 0xF000) == 0xA000) throw new InvalidDataException("Archive contains a symbolic link.");
            expanded = checked(expanded + entry.Length);
            if (expanded > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("Expanded package is too large.");
            var target = Resolve(destination, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var source = entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920]; long written = 0; int count;
            while ((count = source.Read(buffer)) != 0)
            {
                written += count;
                if (written > entry.Length) throw new InvalidDataException("Expanded entry exceeds declared length.");
                output.Write(buffer, 0, count);
            }
            if (written != entry.Length) throw new InvalidDataException("Truncated archive entry.");
        }
        if (remaining.Count != 0) throw new InvalidDataException("Incomplete package.");
    }
}
