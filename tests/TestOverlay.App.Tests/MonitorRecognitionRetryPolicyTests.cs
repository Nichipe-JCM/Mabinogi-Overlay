using TestOverlay.App.Services;
using Xunit;

namespace TestOverlay.App.Tests;

public sealed class MonitorRecognitionRetryPolicyTests
{
    [Fact]
    public void Verification_starts_with_six_immediate_retries()
    {
        var policy = new MonitorRecognitionRetryPolicy();

        for (var attempt = 1; attempt <= 6; attempt++)
        {
            Assert.Equal(TimeSpan.Zero, policy.CompleteAttempt(needsVerification: true));
            Assert.Equal(attempt, policy.ConsecutiveVerificationAttempts);
        }
    }

    [Fact]
    public void Persistent_verification_backs_off_in_two_stages()
    {
        var policy = new MonitorRecognitionRetryPolicy();

        for (var attempt = 1; attempt <= 6; attempt++)
        {
            policy.CompleteAttempt(needsVerification: true);
        }

        Assert.Equal(TimeSpan.FromSeconds(1), policy.CompleteAttempt(needsVerification: true));
        for (var attempt = 8; attempt <= 12; attempt++)
        {
            policy.CompleteAttempt(needsVerification: true);
        }

        Assert.Equal(TimeSpan.FromSeconds(3), policy.CompleteAttempt(needsVerification: true));
    }

    [Fact]
    public void Resolved_verification_resets_the_retry_burst()
    {
        var policy = new MonitorRecognitionRetryPolicy();
        for (var attempt = 1; attempt <= 13; attempt++)
        {
            policy.CompleteAttempt(needsVerification: true);
        }

        Assert.Equal(TimeSpan.FromSeconds(1), policy.CompleteAttempt(needsVerification: false));
        Assert.Equal(0, policy.ConsecutiveVerificationAttempts);
        Assert.Equal(TimeSpan.Zero, policy.CompleteAttempt(needsVerification: true));
    }
}
