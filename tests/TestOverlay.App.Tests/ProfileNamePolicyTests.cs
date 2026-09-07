using TestOverlay.App.Services;
using Xunit;

namespace TestOverlay.App.Tests;

public sealed class ProfileNamePolicyTests
{
    [Theory]
    [InlineData("CON")]
    [InlineData("LPT1")]
    [InlineData("trailing.")]
    [InlineData("bad/name")]
    public void ValidateUserProfileName_RejectsUnsafeWindowsNames(string name)
    {
        Assert.ThrowsAny<Exception>(() => ProfileStore.ValidateUserProfileName(name));
    }

    [Fact]
    public void NormalizeProfileName_MakesImportedReservedNameSafe()
    {
        Assert.Equal("_CON", ProfileStore.NormalizeProfileName("CON"));
    }
}
