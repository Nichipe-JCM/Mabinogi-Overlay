namespace TestOverlay.Update;

public static class UpdateTemporaryFiles
{
    public const string CompleteMarker = "completed.txt";
    public static void Cleanup(string updatesRoot)
    {
        if (!Directory.Exists(updatesRoot)) return;
        PackageFiles.RejectLinks(updatesRoot);
        foreach (var directory in Directory.EnumerateDirectories(updatesRoot))
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) continue;
            try
            {
                PackageFiles.RejectLinks(directory);
                if (!File.Exists(PackageFiles.Resolve(directory, CompleteMarker))) continue;
                foreach (var name in new[] { "package.zip", GitHubUpdateClient.HelperExecutable })
                {
                    var path = PackageFiles.Resolve(directory, name);
                    if (File.Exists(path)) File.Delete(path);
                }
                if (Directory.EnumerateFileSystemEntries(directory).All(path => Path.GetFileName(path) == CompleteMarker))
                {
                    File.Delete(PackageFiles.Resolve(directory, CompleteMarker));
                    Directory.Delete(directory);
                }
            }
            catch (IOException) { } // A still-running helper is retried on a later launch.
            catch (UnauthorizedAccessException) { }
        }
    }
}
