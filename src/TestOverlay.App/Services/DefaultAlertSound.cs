using System.IO;

namespace TestOverlay.App.Services;

internal static class DefaultAlertSound
{
    private const string RelativePath = "Audio/DefaultAlert.mp3";

    public static string? ResolvePath(AppLog? log = null) =>
        ResolvePath(AppContext.BaseDirectory, log);

    internal static string? ResolvePath(string baseDirectory, AppLog? log = null)
    {
        try
        {
            var path = Path.GetFullPath(
                Path.Combine(
                    baseDirectory,
                    RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (File.Exists(path))
            {
                return path;
            }

            log?.Info($"Built-in alert sound unavailable: path={path}");
        }
        catch (Exception exception)
        {
            log?.Error("Failed to resolve the built-in alert sound.", exception);
        }

        return null;
    }
}
