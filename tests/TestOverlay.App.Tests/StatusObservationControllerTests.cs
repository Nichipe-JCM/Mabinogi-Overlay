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
    public void Buff_is_removed_after_five_zero_confirmations()
    {
        var controller = CreateController();
        controller.ObserveBuffTime("buff", 30, "30", "test", 30, StartedAt);

        for (var index = 0; index < 5; index++)
        {
            controller.RegisterBuffZeroConfirmation("buff", "test", "inactive");
        }

        Assert.Empty(controller.Timers);
    }

    private static StatusObservationController CreateController() => new(new AppLog(enabled: false));
}
