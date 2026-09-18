using AppWatcher.Core;
using Xunit;

namespace AppWatcher.Core.Tests;

public sealed class RestartLimiterTests
{
    [Fact]
    public void AllowsConfiguredNumberThenEntersBackoff()
    {
        var definition = new ApplicationDefinition
        {
            RestartLoopProtectionEnabled = true,
            MaxRestarts = 2,
            RestartWindowMinutes = 10,
            BackoffMinutes = 15
        };
        var limiter = new RestartLimiter(definition);
        var now = DateTimeOffset.UtcNow;

        var first = limiter.TryAcquire(now);
        var second = limiter.TryAcquire(now.AddSeconds(1));
        var third = limiter.TryAcquire(now.AddSeconds(2));

        Assert.True(first.Allowed);
        Assert.True(second.Allowed);
        Assert.False(third.Allowed);
        Assert.True(third.EnteredBackoff);
        Assert.Equal("RestartLimitExceeded", third.ReasonCode);
        Assert.NotNull(third.BackoffUntilUtc);
    }

    [Fact]
    public void RejectsRepeatedAttemptsWhileBackoffIsActive()
    {
        var definition = new ApplicationDefinition
        {
            RestartLoopProtectionEnabled = true,
            MaxRestarts = 1,
            RestartWindowMinutes = 10,
            BackoffMinutes = 5
        };
        var limiter = new RestartLimiter(definition);
        var now = DateTimeOffset.UtcNow;

        Assert.True(limiter.TryAcquire(now).Allowed);

        var entered = limiter.TryAcquire(now.AddSeconds(1));
        var stillBlocked = limiter.TryAcquire(now.AddMinutes(1));

        Assert.False(entered.Allowed);
        Assert.True(entered.EnteredBackoff);
        Assert.False(stillBlocked.Allowed);
        Assert.False(stillBlocked.EnteredBackoff);
        Assert.Equal("BackoffActive", stillBlocked.ReasonCode);
        Assert.Equal(entered.BackoffUntilUtc, stillBlocked.BackoffUntilUtc);
    }

    [Fact]
    public void AllowsRestartAfterBackoffExpiresAndWindowIsPruned()
    {
        var definition = new ApplicationDefinition
        {
            RestartLoopProtectionEnabled = true,
            MaxRestarts = 1,
            RestartWindowMinutes = 1,
            BackoffMinutes = 2
        };
        var limiter = new RestartLimiter(definition);
        var now = DateTimeOffset.UtcNow;

        Assert.True(limiter.TryAcquire(now).Allowed);
        Assert.False(limiter.TryAcquire(now.AddSeconds(1)).Allowed);

        var afterBackoff = limiter.TryAcquire(now.AddMinutes(2).AddSeconds(2));

        Assert.True(afterBackoff.Allowed);
        Assert.False(afterBackoff.EnteredBackoff);
        Assert.Equal("RestartAllowed", afterBackoff.ReasonCode);
    }

    [Fact]
    public void ResetHealthyClearsAttemptsAndBackoff()
    {
        var definition = new ApplicationDefinition
        {
            RestartLoopProtectionEnabled = true,
            MaxRestarts = 1,
            RestartWindowMinutes = 10,
            BackoffMinutes = 5
        };
        var limiter = new RestartLimiter(definition);
        var now = DateTimeOffset.UtcNow;

        Assert.True(limiter.TryAcquire(now).Allowed);
        Assert.False(limiter.TryAcquire(now.AddSeconds(1)).Allowed);
        Assert.NotNull(limiter.BackoffUntilUtc);

        limiter.ResetHealthy();

        Assert.Equal(0, limiter.AttemptsInWindow);
        Assert.Null(limiter.BackoffUntilUtc);
        Assert.True(limiter.TryAcquire(now.AddSeconds(2)).Allowed);
    }

    [Fact]
    public void DisabledLoopProtectionNeverBlocksRestart()
    {
        var definition = new ApplicationDefinition
        {
            RestartLoopProtectionEnabled = false,
            MaxRestarts = 1,
            RestartWindowMinutes = 1,
            BackoffMinutes = 60
        };
        var limiter = new RestartLimiter(definition);
        var now = DateTimeOffset.UtcNow;

        for (var i = 0; i < 20; i++)
        {
            var permission = limiter.TryAcquire(now.AddSeconds(i));
            Assert.True(permission.Allowed);
            Assert.False(permission.EnteredBackoff);
            Assert.Equal("LoopProtectionDisabled", permission.ReasonCode);
        }
    }
}
