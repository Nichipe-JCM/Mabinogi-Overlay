using System.Windows;
using TestOverlay.App.Models;
using TestOverlay.App.Services;
using Xunit;

namespace TestOverlay.App.Tests;

public sealed class BuffAnchorMatchResolverTests
{
    [Fact]
    public void Any_active_alias_makes_the_logical_buff_active()
    {
        var burning = CreateMatch("ham-burning", isActive: false, confidence: 0.4);
        var adrenaline = CreateMatch("ham-adrenaline", isActive: true, confidence: 0.3);

        var state = BuffAnchorMatchResolver.ClassifyLogicalState(
            [burning, adrenaline],
            [burning, adrenaline]);

        Assert.Equal(BuffAnchorObservationState.Active, state);
    }

    [Fact]
    public void Every_alias_must_be_observed_inactive_before_logical_buff_is_inactive()
    {
        var burning = CreateMatch("ham-burning", isActive: false, confidence: 0.4);
        var adrenaline = CreateMatch("ham-adrenaline", isActive: false, confidence: 0.3);

        Assert.Equal(
            BuffAnchorObservationState.Indeterminate,
            BuffAnchorMatchResolver.ClassifyLogicalState([burning, adrenaline], [burning]));
        Assert.Equal(
            BuffAnchorObservationState.Inactive,
            BuffAnchorMatchResolver.ClassifyLogicalState(
                [burning, adrenaline],
                [burning, adrenaline]));
    }

    [Fact]
    public void Alias_templates_have_distinct_anchor_keys()
    {
        var burning = CreateMatch("ham-burning", isActive: true, confidence: 0.2);
        var adrenaline = CreateMatch("ham-adrenaline", isActive: true, confidence: 0.2);

        Assert.NotEqual(
            BuffAnchorMatchResolver.AnchorKey(burning),
            BuffAnchorMatchResolver.AnchorKey(adrenaline));
    }

    [Fact]
    public void Highest_confidence_active_alias_is_selected_for_ocr()
    {
        var burning = CreateMatch("ham-burning", isActive: true, confidence: 0.2);
        var adrenaline = CreateMatch("ham-adrenaline", isActive: true, confidence: 0.5);

        Assert.Same(
            adrenaline,
            BuffAnchorMatchResolver.SelectActiveMatch([burning, adrenaline]));
    }

    private static BuffIconMatch CreateMatch(string templateId, bool isActive, double confidence) =>
        new(
            MonitoredBuffCatalog.Hamjji,
            new Rect(1, 2, 16, 16),
            0.9,
            isActive,
            confidence,
            templateId);
}
