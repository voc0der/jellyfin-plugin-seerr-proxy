using Jellyfin.Plugin.SeerrProxy.Security;

namespace Jellyfin.Plugin.SeerrProxy.Tests;

public class SeerrProxyRateLimiterTests
{
    private static readonly TimeSpan LongWindow = TimeSpan.FromMinutes(5);

    [Fact]
    public void TryAcquireProxy_WithinLimit_Succeeds()
    {
        using var limiter = new SeerrProxyRateLimiter(3, 3, LongWindow);

        Assert.True(limiter.TryAcquireProxy("user-a", out _));
        Assert.True(limiter.TryAcquireProxy("user-a", out _));
        Assert.True(limiter.TryAcquireProxy("user-a", out _));
    }

    [Fact]
    public void TryAcquireProxy_BeyondLimit_IsRefused()
    {
        using var limiter = new SeerrProxyRateLimiter(2, 2, LongWindow);

        Assert.True(limiter.TryAcquireProxy("user-a", out _));
        Assert.True(limiter.TryAcquireProxy("user-a", out _));
        Assert.False(limiter.TryAcquireProxy("user-a", out var retryAfter));
        Assert.True(retryAfter > TimeSpan.Zero);
    }

    [Fact]
    public void TryAcquireProxy_OneUserCannotStarveAnother()
    {
        using var limiter = new SeerrProxyRateLimiter(2, 2, LongWindow);

        Assert.True(limiter.TryAcquireProxy("noisy", out _));
        Assert.True(limiter.TryAcquireProxy("noisy", out _));
        Assert.False(limiter.TryAcquireProxy("noisy", out _));

        // The quiet user's window is untouched.
        Assert.True(limiter.TryAcquireProxy("quiet", out _));
        Assert.True(limiter.TryAcquireProxy("quiet", out _));
    }

    [Fact]
    public void TryAcquireAdmin_IsProcessWide()
    {
        using var limiter = new SeerrProxyRateLimiter(100, 2, LongWindow);

        Assert.True(limiter.TryAcquireAdmin(out _));
        Assert.True(limiter.TryAcquireAdmin(out _));
        Assert.False(limiter.TryAcquireAdmin(out var retryAfter));
        Assert.True(retryAfter > TimeSpan.Zero);
    }

    [Fact]
    public void AdminAndProxyBudgets_AreIndependent()
    {
        using var limiter = new SeerrProxyRateLimiter(2, 1, LongWindow);

        Assert.True(limiter.TryAcquireAdmin(out _));
        Assert.False(limiter.TryAcquireAdmin(out _));

        // Exhausting the elevated budget must not lock out ordinary users.
        Assert.True(limiter.TryAcquireProxy("user-a", out _));
    }

    [Fact]
    public void Window_Replenishes()
    {
        using var limiter = new SeerrProxyRateLimiter(1, 1, TimeSpan.FromMilliseconds(150));

        Assert.True(limiter.TryAcquireProxy("user-a", out _));
        Assert.False(limiter.TryAcquireProxy("user-a", out _));

        Thread.Sleep(400);

        Assert.True(limiter.TryAcquireProxy("user-a", out _));
    }

    [Fact]
    public void Defaults_AreGenerousForUsersAndTightForOperators()
    {
        Assert.Equal(120, SeerrProxyRateLimiter.DefaultProxyPermitLimit);
        Assert.Equal(30, SeerrProxyRateLimiter.DefaultAdminPermitLimit);
        Assert.True(SeerrProxyRateLimiter.DefaultAdminPermitLimit < SeerrProxyRateLimiter.DefaultProxyPermitLimit);
    }
}
