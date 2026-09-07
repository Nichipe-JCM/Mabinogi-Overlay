using System.IO;
using System.Security.Cryptography;
using System.Text;
using TestOverlay.App.Models;

namespace TestOverlay.App.Services;

// Opt-in only: normal launches ignore the variable and all injected fault files.
public static class TestLabEnvironment
{
    public const string RootVariable = "MABINOGI_OVERLAY_TEST_ROOT";
    public const string TargetTitle = "Overlay Capture Test Target";
    public static string? Root { get; } = ResolveRoot(Environment.GetCommandLineArgs(), Environment.GetEnvironmentVariable(RootVariable));
    public static bool Enabled => Root is not null;
    public static string InstanceName => Enabled
        ? @"Local\Nichipe.MabinogiOverlay.Test." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Root!.ToUpperInvariant())))[..16]
        : @"Local\Nichipe.MabinogiOverlay";

    internal static string? ResolveRoot(string[] args, string? root) =>
        args.Contains("--test-lab", StringComparer.Ordinal) && !string.IsNullOrWhiteSpace(root) && Path.IsPathFullyQualified(root)
            ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) : null;

    public static bool IsTarget(GameWindowInfo window) => Enabled &&
        window.Title == TargetTitle && window.ProcessExecutableName.Equals("TestOverlay.TestLab.exe", StringComparison.OrdinalIgnoreCase);

    public static bool Fault(string name) => Enabled && File.Exists(Path.Combine(Root!, "faults", name));

    private static int _activations;
    public static void RecordActivation()
    {
        if (Enabled) File.WriteAllText(Path.Combine(Root!, "activations.txt"), Interlocked.Increment(ref _activations).ToString());
    }

    public static void CheckSave(string path)
    {
        if (!Enabled) return;
        var name = Path.GetFileName(path);
        if ((name == "erin-timer.json" && Fault("erin-save")) ||
            (Path.GetDirectoryName(Path.GetFullPath(path)) == Path.Combine(Root!, "Profiles") && Fault("profile-save")))
            throw new IOException("TEST LAB: simulated storage write failure.");
    }
}
