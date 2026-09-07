using System.IO;
using TestOverlay.App.Services;
using Xunit;

namespace TestOverlay.App.Tests;

public sealed class TestLabEnvironmentTests
{
    [Fact]
    public void IsolationRequiresBothExplicitFlagAndAbsoluteDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "overlay-test-lab");
        Assert.Null(TestLabEnvironment.ResolveRoot([], root));
        Assert.Null(TestLabEnvironment.ResolveRoot(["--test-lab"], null));
        Assert.Null(TestLabEnvironment.ResolveRoot(["--test-lab"], "relative"));
        Assert.Equal(root, TestLabEnvironment.ResolveRoot(["--test-lab"], root));
        Assert.False(TestLabEnvironment.Enabled);
        Assert.False(TestLabEnvironment.Fault("profile-save"));
    }
}
