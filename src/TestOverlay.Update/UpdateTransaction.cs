using System.Text.Json;

namespace TestOverlay.Update;

public sealed class UpdateTransaction
{
    public sealed record Entry(string Name, bool Existed);
    public sealed record Journal(string Target, string State, Entry[] Entries);
    private readonly string _target;
    public string WorkDirectory { get; }
    private readonly List<Entry> _entries = [];
    public UpdateTransaction(string target)
    {
        _target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        PackageFiles.RejectLinks(_target);
        WorkDirectory = Path.Combine(_target, ".overlay-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(WorkDirectory);
        WriteJournal("Prepared");
    }
    public static string[] Pending(string target) => Directory.GetDirectories(target, ".overlay-update-*")
        .Where(dir => File.Exists(Path.Combine(dir, "journal.json")) && ReadJournal(dir).State is not ("Complete" or "RolledBack")).ToArray();
    public void Apply(string staged, string[] previousFiles, string[] nextFiles, Action<string>? progress = null)
    {
        PackageFiles.Validate(previousFiles); PackageFiles.Validate(nextFiles);
        var old = new HashSet<string>(previousFiles, StringComparer.OrdinalIgnoreCase);
        // Newly introduced product names must not silently overwrite user-owned files.
        foreach (var name in nextFiles)
            if (!old.Contains(name) && File.Exists(PackageFiles.Resolve(_target, name))) throw new IOException("A personal file conflicts with a new product file: " + name);
        try
        {
            foreach (var name in previousFiles.Union(nextFiles, StringComparer.OrdinalIgnoreCase))
            {
                var destination = PackageFiles.Resolve(_target, name);
                var backup = PackageFiles.Resolve(Path.Combine(WorkDirectory, "backup"), name);
                var source = PackageFiles.Resolve(staged, name);
                var existed = File.Exists(destination);
                _entries.Add(new Entry(name, existed));
                WriteJournal("Applying"); // Intent is durable before modifying this file.
                if (existed)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Move(destination, backup);
                }
                if (nextFiles.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Move(source, destination);
                }
                progress?.Invoke(name);
            }
            WriteJournal("Applied");
        }
        catch { Rollback(WorkDirectory, _target); throw; }
    }
    public void Complete() => WriteJournal("Complete");
    public void CleanupCompleted()
    {
        var journal = ReadJournal(WorkDirectory);
        if (journal.State != "Complete" || journal.Target != _target) throw new InvalidOperationException("Only a completed update can be cleaned up.");
        // Delete only files owned by this transaction, preserving any unexpected files.
        foreach (var entry in journal.Entries)
        {
            var backup = PackageFiles.Resolve(WorkDirectory, "backup/" + entry.Name);
            if (File.Exists(backup)) File.Delete(backup);
        }
        var zip = PackageFiles.Resolve(WorkDirectory, "package.zip");
        if (File.Exists(zip)) File.Delete(zip);
        RemoveEmptyDirectories(WorkDirectory);
        if (Directory.EnumerateFileSystemEntries(WorkDirectory).All(path => Path.GetFileName(path) == "journal.json"))
        {
            File.Delete(PackageFiles.Resolve(WorkDirectory, "journal.json"));
            Directory.Delete(WorkDirectory);
        }
    }
    private static void RemoveEmptyDirectories(string root)
    {
        PackageFiles.RejectLinks(root);
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            RemoveEmptyDirectories(directory);
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
    }
    public static void Rollback(string work, string expectedTarget)
    {
        var journal = ReadJournal(work);
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedTarget));
        if (!string.Equals(journal.Target, target, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetDirectoryName(Path.GetFullPath(work)), target, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(work).StartsWith(".overlay-update-", StringComparison.Ordinal)) throw new InvalidDataException("Invalid recovery target.");
        PackageFiles.RejectLinks(work);
        if (journal.State is "Complete" or "RolledBack") return;
        foreach (var entry in journal.Entries.Reverse())
        {
            var destination = PackageFiles.Resolve(target, entry.Name);
            var backup = PackageFiles.Resolve(Path.Combine(work, "backup"), entry.Name);
            if (entry.Existed)
            {
                // A missing backup means the original move never happened or was already restored.
                if (!File.Exists(backup)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(backup, destination, true);
            }
            else if (File.Exists(destination)) File.Delete(destination);
        }
        SaveJournal(work, journal with { State = "RolledBack" });
    }
    private void WriteJournal(string state) => SaveJournal(WorkDirectory, new Journal(_target, state, _entries.ToArray()));
    private static Journal ReadJournal(string directory)
    {
        PackageFiles.RejectLinks(directory);
        var path = Path.Combine(directory, "journal.json");
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Recovery journal is too large.");
        return JsonSerializer.Deserialize<Journal>(File.ReadAllText(path)) ?? throw new InvalidDataException("Invalid recovery journal.");
    }
    private static void SaveJournal(string directory, Journal journal)
    {
        var path = Path.Combine(directory, "journal.json");
        using (var file = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None)) { JsonSerializer.Serialize(file, journal); file.Flush(true); }
        File.Move(path + ".tmp", path, true);
    }
}
