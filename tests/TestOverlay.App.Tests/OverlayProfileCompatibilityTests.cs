using System.Text.Json;
using TestOverlay.App.Models;
using Xunit;

namespace TestOverlay.App.Tests;

public sealed class OverlayProfileCompatibilityTests
{
    [Fact]
    public void LegacyTuarimProperty_LoadsIntoCorrectedTuairimSetting()
    {
        var profile = JsonSerializer.Deserialize<OverlayProfile>(
            """
            {
              "Name": "legacy",
              "TuarimMonitorEnabled": true
            }
            """);

        Assert.NotNull(profile);
        Assert.True(profile.TuairimMonitorEnabled);
    }

    [Fact]
    public void SavingProfile_DoesNotWriteLegacyTuarimProperty()
    {
        var profile = new OverlayProfile { TuairimMonitorEnabled = true };

        var json = JsonSerializer.Serialize(profile);
        using var document = JsonDocument.Parse(json);

        Assert.True(document.RootElement.GetProperty("TuairimMonitorEnabled").GetBoolean());
        Assert.False(document.RootElement.TryGetProperty("TuarimMonitorEnabled", out _));
    }
}
