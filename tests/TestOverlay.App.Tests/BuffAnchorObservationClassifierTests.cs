using System.Windows;
using TestOverlay.App.Models;
using TestOverlay.App.Services;
using Xunit;

namespace TestOverlay.App.Tests;

public sealed class BuffAnchorObservationClassifierTests
{
    [Fact]
    public void Missing_anchor_is_indeterminate()
    {
        Assert.Equal(
            BuffAnchorObservationState.Indeterminate,
            BuffAnchorObservationClassifier.Classify(null));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Low_confidence_is_indeterminate_regardless_of_binary_match(bool isActive)
    {
        var match = CreateMatch(isActive, BuffAnchorObservationClassifier.MinimumStateConfidence - 0.001);

        Assert.Equal(
            BuffAnchorObservationState.Indeterminate,
            BuffAnchorObservationClassifier.Classify(match));
    }

    [Fact]
    public void Confident_active_match_is_active()
    {
        var match = CreateMatch(true, BuffAnchorObservationClassifier.MinimumStateConfidence);

        Assert.Equal(
            BuffAnchorObservationState.Active,
            BuffAnchorObservationClassifier.Classify(match));
    }

    [Fact]
    public void Confident_inactive_match_is_inactive()
    {
        var match = CreateMatch(false, BuffAnchorObservationClassifier.MinimumStateConfidence);

        Assert.Equal(
            BuffAnchorObservationState.Inactive,
            BuffAnchorObservationClassifier.Classify(match));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Invalid_confidence_is_indeterminate(double confidence)
    {
        Assert.Equal(
            BuffAnchorObservationState.Indeterminate,
            BuffAnchorObservationClassifier.Classify(CreateMatch(true, confidence)));
    }

    private static BuffIconMatch CreateMatch(bool isActive, double confidence) =>
        new("buff", new Rect(1, 2, 3, 4), 0.9, isActive, confidence);
}
