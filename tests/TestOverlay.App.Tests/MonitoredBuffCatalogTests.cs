using TestOverlay.App.Services;
using Xunit;

namespace TestOverlay.App.Tests;

public sealed class MonitoredBuffCatalogTests
{
    [Fact]
    public void Status_buffs_are_unrestricted_by_existing_selections()
    {
        var selected = new[]
        {
            MonitoredBuffCatalog.BattleOverture,
            MonitoredBuffCatalog.MarchSong,
            MonitoredBuffCatalog.DivineLink,
            MonitoredBuffCatalog.ConditionSupport
        };

        Assert.True(MonitoredBuffCatalog.CanAddSelection(selected, MonitoredBuffCatalog.PurificationWave));
        Assert.True(MonitoredBuffCatalog.CanAddSelection(selected, MonitoredBuffCatalog.Hamjji));
    }

    [Fact]
    public void Status_buffs_do_not_count_toward_music_limit()
    {
        var selected = new[]
        {
            MonitoredBuffCatalog.DivineLink,
            MonitoredBuffCatalog.ConditionSupport,
            MonitoredBuffCatalog.PurificationWave
        };

        Assert.True(MonitoredBuffCatalog.CanAddSelection(selected, MonitoredBuffCatalog.BattleOverture));
        Assert.True(MonitoredBuffCatalog.IsStatusBuff(MonitoredBuffCatalog.Hamjji));
    }

    [Fact]
    public void Two_music_buffs_require_march_song()
    {
        var withBattle = new[]
        {
            MonitoredBuffCatalog.BattleOverture,
            MonitoredBuffCatalog.DivineLink
        };

        Assert.False(MonitoredBuffCatalog.CanAddSelection(withBattle, MonitoredBuffCatalog.Vivace));
        Assert.True(MonitoredBuffCatalog.CanAddSelection(withBattle, MonitoredBuffCatalog.MarchSong));
    }

    [Fact]
    public void Third_music_buff_is_rejected_even_with_status_buffs_selected()
    {
        var selected = new[]
        {
            MonitoredBuffCatalog.BattleOverture,
            MonitoredBuffCatalog.MarchSong,
            MonitoredBuffCatalog.DivineLink
        };

        Assert.False(MonitoredBuffCatalog.CanAddSelection(selected, MonitoredBuffCatalog.HarvestSong));
    }
}
