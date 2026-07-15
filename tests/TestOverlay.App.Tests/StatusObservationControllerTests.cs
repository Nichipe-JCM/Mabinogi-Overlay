using TestOverlay.App.Services;
using Xunit;

namespace TestOverlay.App.Tests;

public sealed class StatusObservationControllerTests
{
    private static readonly DateTimeOffset StartedAt =
        new(2026, 7, 14, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Tuairim_initial_value_requires_two_consistent_observations()
    {
        var controller = CreateController();

        var first = controller.ObserveTuairimPercent(50, "test", StartedAt);
        var second = controller.ObserveTuairimPercent(53, "test", StartedAt.AddSeconds(2));

        Assert.False(first.Accepted);
        Assert.True(second.Accepted);
        Assert.Equal(53, controller.TuairimPercent);
        Assert.False(second.HadAcceptedObservation);
    }

    [Fact]
    public void Tuairim_rejects_implausible_jump_and_requires_zero_confirmation()
    {
        var controller = CreateController();
        controller.SetTuairimPercentForTest(50, StartedAt);

        var jump = controller.ObserveTuairimPercent(70, "test", StartedAt.AddSeconds(2));
        var firstZero = controller.ObserveTuairimPercent(0, "test", StartedAt.AddSeconds(4));
        var secondZero = controller.ObserveTuairimPercent(0, "test", StartedAt.AddSeconds(6));

        Assert.True(jump.Rejected);
        Assert.False(firstZero.Accepted);
        Assert.True(secondZero.Accepted);
        Assert.Equal(0, controller.TuairimPercent);
    }

    [Fact]
    public void Buff_rejects_short_initial_value_while_activation_is_pending()
    {
        var controller = CreateController();
        controller.PendingInitialBuffValidation.Add("buff");

        var result = controller.ObserveBuffTime("buff", 45, "45", "test", 30, StartedAt);

        Assert.False(result.Accepted);
        Assert.Empty(controller.Timers);
        Assert.True(controller.NeedsVerification);
    }

    [Fact]
    public void Buff_accepts_sustained_downward_observation_only_after_five_seconds()
    {
        var controller = CreateController();
        controller.ObserveBuffTime("buff", 100, "100", "test", 30, StartedAt);

        for (var index = 0; index < 4; index++)
        {
            controller.ObserveBuffTime(
                "buff",
                80 - index,
                (80 - index).ToString(),
                "test",
                30,
                StartedAt.AddSeconds(index));
        }

        Assert.Equal(100, controller.Timers.Single().RemainingSeconds);

        controller.ObserveBuffTime("buff", 76, "76", "test", 30, StartedAt.AddSeconds(5));

        Assert.Equal(76, controller.Timers.Single().RemainingSeconds);
    }

    [Fact]
    public void Buff_corrects_small_timer_lead_after_two_consistent_observations()
    {
        var controller = CreateController();
        controller.ObserveBuffTime(
            MonitoredBuffCatalog.BattleOverture,
            316,
            "5분 16초",
            "test",
            30,
            StartedAt);

        controller.ObserveBuffTime(
            MonitoredBuffCatalog.BattleOverture,
            307,
            "5분 7초",
            "test",
            30,
            StartedAt.AddSeconds(1));
        Assert.Equal(316, controller.Timers.Single().RemainingSeconds);

        controller.ObserveBuffTime(
            MonitoredBuffCatalog.BattleOverture,
            306,
            "5분 6초",
            "test",
            30,
            StartedAt.AddSeconds(2));

        Assert.Equal(306, controller.Timers.Single().RemainingSeconds);
        Assert.False(controller.NeedsVerification);
    }

    [Fact]
    public void Buff_is_removed_after_five_zero_confirmations()
    {
        var controller = CreateController();
        controller.ObserveBuffTime("buff", 30, "30", "test", 30, StartedAt);

        for (var index = 0; index < 5; index++)
        {
            controller.ObserveBuffAnchorState("buff", BuffAnchorObservationState.Inactive, "test");
        }

        Assert.Empty(controller.Timers);
    }

    [Fact]
    public void Indeterminate_anchor_preserves_zero_confirmation_progress()
    {
        var controller = CreateController();
        controller.ObserveBuffTime("buff", 30, "30", "test", 30, StartedAt);
        controller.ObserveBuffAnchorState("buff", BuffAnchorObservationState.Inactive, "test");
        controller.ObserveBuffAnchorState("buff", BuffAnchorObservationState.Inactive, "test");

        var shouldReadValue = controller.ObserveBuffAnchorState(
            "buff",
            BuffAnchorObservationState.Indeterminate,
            "test");

        Assert.False(shouldReadValue);
        Assert.Single(controller.Timers);
        Assert.Equal(2, controller.Timers[0].ConsecutiveZeroConfirmations);
    }

    [Fact]
    public void Confident_active_anchor_resets_zero_confirmation_progress()
    {
        var controller = CreateController();
        controller.ObserveBuffTime("buff", 30, "30", "test", 30, StartedAt);
        controller.ObserveBuffAnchorState("buff", BuffAnchorObservationState.Inactive, "test");
        controller.ObserveBuffAnchorState("buff", BuffAnchorObservationState.Inactive, "test");

        var shouldReadValue = controller.ObserveBuffAnchorState(
            "buff",
            BuffAnchorObservationState.Active,
            "test");

        Assert.True(shouldReadValue);
        Assert.Equal(0, controller.Timers[0].ConsecutiveZeroConfirmations);
    }

    [Fact]
    public void Status_buff_refresh_requires_three_seconds_of_consistent_observations()
    {
        var controller = CreateController();
        var key = MonitoredBuffCatalog.DivineLink;
        controller.ObserveBuffTime(key, 100, "100", "test", 30, StartedAt);

        controller.ObserveBuffTime(key, 200, "200", "test", 30, StartedAt.AddSeconds(1));
        controller.ObserveBuffTime(key, 198, "198", "test", 30, StartedAt.AddSeconds(3));
        Assert.Equal(100, controller.Timers.Single().RemainingSeconds);

        controller.ObserveBuffTime(key, 197, "197", "test", 30, StartedAt.AddSeconds(4));

        Assert.Equal(197, controller.Timers.Single().RemainingSeconds);
    }

    [Fact]
    public void Status_buff_accepts_large_downward_value_after_sustained_confirmation()
    {
        var controller = CreateController();
        var key = MonitoredBuffCatalog.ConditionSupport;
        controller.ObserveBuffTime(key, 100, "100", "test", 30, StartedAt);

        controller.ObserveBuffTime(key, 50, "50", "test", 30, StartedAt.AddSeconds(1));
        controller.ObserveBuffTime(key, 48, "48", "test", 30, StartedAt.AddSeconds(3));
        Assert.Equal(100, controller.Timers.Single().RemainingSeconds);

        controller.ObserveBuffTime(key, 46, "46", "test", 30, StartedAt.AddSeconds(5));

        Assert.Equal(46, controller.Timers.Single().RemainingSeconds);
        Assert.False(controller.NeedsVerification);
    }

    [Fact]
    public void Status_buff_rejects_isolated_large_downward_value()
    {
        var controller = CreateController();
        var key = MonitoredBuffCatalog.DivineLink;
        controller.ObserveBuffTime(key, 100, "100", "test", 30, StartedAt);

        controller.ObserveBuffTime(key, 50, "50", "test", 30, StartedAt.AddSeconds(1));

        Assert.Equal(100, controller.Timers.Single().RemainingSeconds);
        Assert.True(controller.NeedsVerification);
    }

    [Fact]
    public void Status_buff_inactive_state_requires_three_seconds_before_removal()
    {
        var controller = CreateController();
        var key = MonitoredBuffCatalog.PurificationWave;
        controller.ObserveBuffTime(key, 100, "100", "test", 30, StartedAt);

        controller.ObserveBuffAnchorState(key, BuffAnchorObservationState.Inactive, "test", StartedAt.AddSeconds(1));
        controller.ObserveBuffAnchorState(key, BuffAnchorObservationState.Inactive, "test", StartedAt.AddSeconds(2));
        Assert.Single(controller.Timers);

        controller.ObserveBuffAnchorState(key, BuffAnchorObservationState.Inactive, "test", StartedAt.AddSeconds(4));

        Assert.Empty(controller.Timers);
    }

    [Fact]
    public void Status_buff_zero_expires_immediately()
    {
        var controller = CreateController();
        var key = MonitoredBuffCatalog.DivineLink;
        controller.ObserveBuffTime(key, 100, "100", "test", 30, StartedAt);

        controller.ObserveBuffTime(key, 0, "0", "test", 30, StartedAt.AddSeconds(1));

        Assert.Empty(controller.Timers);
    }

    [Fact]
    public void Status_buff_countdown_zero_expires_immediately()
    {
        var controller = CreateController();
        var key = MonitoredBuffCatalog.ConditionSupport;
        controller.ObserveBuffTime(key, 1, "1", "test", 30, StartedAt);

        var expired = controller.ExpireBuffAtCountdownZero(key);

        Assert.True(expired);
        Assert.Empty(controller.Timers);
    }

    [Fact]
    public void Status_buff_ignores_music_extension_tags()
    {
        var controller = CreateController();

        controller.ObserveBuffTime(
            MonitoredBuffCatalog.DivineLink,
            100,
            "투안의 노래 하모니 1:40",
            "test",
            30,
            StartedAt);

        var timer = Assert.Single(controller.Timers);
        Assert.False(timer.HasTuanExtension);
        Assert.False(timer.HasHarmony);
    }

    private static StatusObservationController CreateController() => new(new AppLog(enabled: false));
}
