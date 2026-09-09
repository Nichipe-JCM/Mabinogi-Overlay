using TestOverlay.Update;
using Xunit;

namespace TestOverlay.App.Tests;

public sealed class UpdateVersionTests
{
    [Theory]
    [InlineData("0.0.10-beta", "0.0.9-beta", 1)]
    [InlineData("0.0.7", "0.0.7-beta", 1)]
    [InlineData("v0.0.7-beta.10", "0.0.7-beta.2", 1)]
    [InlineData("0.0.4.3", "0.0.4.2", 1)]
    [InlineData("0.0.7+abc", "0.0.7+def", 0)]
    [InlineData("0.0.7-beta", "0.0.7-rc", -1)]
    [InlineData("0.0.7-1", "0.0.7-beta", -1)]
    [InlineData("0.0.7", "0.0.7.0", 0)]
    public void OrdersReleaseVersions(string left, string right, int expected) =>
        Assert.Equal(expected, Math.Sign(UpdateVersion.Parse(left).CompareTo(UpdateVersion.Parse(right))));

    [Theory]
    [InlineData("latest")]
    [InlineData("0.0")]
    [InlineData("0.0.7-beta.01")]
    [InlineData("0.00.7")]
    [InlineData("../0.0.7")]
    public void RejectsAmbiguousVersions(string version) => Assert.Throws<FormatException>(() => UpdateVersion.Parse(version));
}
