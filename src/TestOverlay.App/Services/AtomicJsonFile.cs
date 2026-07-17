using System.IO;
using System.Text;
using System.Text.Json;

namespace TestOverlay.App.Services;

internal static class AtomicJsonFile
{
    public static string GetBackupPath(string path) => $"{path}.bak";

    public static bool Exists(string path) =>
        File.Exists(path) || File.Exists(GetBackupPath(path));

    public static void Save<T>(string path, T value, JsonSerializerOptions options)
    {
        var directory = Path.GetDirectoryName(path) ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(directory);

        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var json = JsonSerializer.Serialize(value, options);
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                File.Replace(temporaryPath, path, GetBackupPath(path), ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, path);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static AtomicJsonLoadResult<T>? Load<T>(
        string path,
        JsonSerializerOptions options,
        Action<T>? validate = null)
        where T : class
    {
        var backupPath = GetBackupPath(path);
        if (!File.Exists(path) && !File.Exists(backupPath))
        {
            return null;
        }

        Exception? primaryException = null;
        if (File.Exists(path) &&
            File.Exists(backupPath) &&
            File.GetLastWriteTimeUtc(backupPath) > File.GetLastWriteTimeUtc(path))
        {
            try
            {
                var newerBackup = Read(backupPath, options, validate);
                RestorePrimaryFromBackup(path, backupPath);
                return new AtomicJsonLoadResult<T>(newerBackup, true, null);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                // A newer timestamp alone is not enough to trust an invalid backup.
            }
        }

        if (File.Exists(path))
        {
            try
            {
                return new AtomicJsonLoadResult<T>(Read(path, options, validate), false, null);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                primaryException = exception;
            }
        }

        if (!File.Exists(backupPath))
        {
            throw new InvalidDataException($"The JSON file is invalid and no backup is available: {path}", primaryException);
        }

        try
        {
            var recovered = Read(backupPath, options, validate);
            RestorePrimaryFromBackup(path, backupPath);
            return new AtomicJsonLoadResult<T>(recovered, true, primaryException);
        }
        catch (Exception backupException) when (backupException is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            throw new InvalidDataException(
                $"Both the JSON file and its backup are invalid: {path}",
                new AggregateException(primaryException ?? new FileNotFoundException(path), backupException));
        }
    }

    private static T Read<T>(string path, JsonSerializerOptions options, Action<T>? validate)
        where T : class
    {
        var value = JsonSerializer.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8), options)
                    ?? throw new InvalidDataException($"The JSON document contains no data: {path}");
        validate?.Invoke(value);
        return value;
    }

    private static void RestorePrimaryFromBackup(string path, string backupPath)
    {
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.restore.tmp";
        try
        {
            File.Copy(backupPath, temporaryPath, overwrite: false);
            if (File.Exists(path))
            {
                File.Replace(temporaryPath, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, path);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}

internal sealed record AtomicJsonLoadResult<T>(
    T Value,
    bool RecoveredFromBackup,
    Exception? PrimaryException)
    where T : class;
