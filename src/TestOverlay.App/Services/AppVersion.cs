using System.Reflection;

namespace TestOverlay.App.Services;

public static class AppVersion
{
    public static string DisplayVersion
    {
        get
        {
            var assembly = typeof(AppVersion).Assembly;
            var informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
            {
                var metadata = informational.IndexOf('+');
                return metadata >= 0 ? informational[..metadata] : informational;
            }

            return assembly.GetName().Version?.ToString(4) ?? "unknown";
        }
    }
}
