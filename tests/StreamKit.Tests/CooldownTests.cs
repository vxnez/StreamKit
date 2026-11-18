using StreamKit.Core.Commands;
using Xunit;

namespace StreamKit.Tests;

public class CooldownTests
{
    [Fact]
    public void BlocksUntilWindowExpires()
    {
        var clock = new FakeClock();
        var tracker = new CooldownTracker(clock);

        Assert.True(tracker.TryAcquire("k", TimeSpan.FromSeconds(10)));
        Assert.False(tracker.TryAcquire("k", TimeSpan.FromSeconds(10)));

        clock.AdvanceSeconds(9.9);
        Assert.False(tracker.TryAcquire("k", TimeSpan.FromSeconds(10)));

        clock.AdvanceSeconds(0.2);
        Assert.True(tracker.TryAcquire("k", TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void KeysAreIndependent()
    {
        var clock = new FakeClock();
        var tracker = new CooldownTracker(clock);

        Assert.True(tracker.TryAcquire("a", TimeSpan.FromSeconds(5)));
        Assert.True(tracker.TryAcquire("b", TimeSpan.FromSeconds(5)));
        Assert.False(tracker.TryAcquire("a", TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void ZeroWindowNeverBlocks()
    {
        var tracker = new CooldownTracker(new FakeClock());
        Assert.True(tracker.TryAcquire("x", TimeSpan.Zero));
        Assert.True(tracker.TryAcquire("x", TimeSpan.Zero));
    }

    [Fact]
    public void RemainingReportsTimeLeft()
    {
        var clock = new FakeClock();
        var tracker = new CooldownTracker(clock);
        tracker.TryAcquire("k", TimeSpan.FromSeconds(10));

        clock.AdvanceSeconds(4);
        var remaining = tracker.Remaining("k");

        Assert.NotNull(remaining);
        Assert.InRange(remaining!.Value.TotalSeconds, 5.9, 6.1);
    }

    [Fact]
    public void RateLimiterAllowsUpToMaxThenResets()
    {
        var clock = new FakeClock();
        var limiter = new RateLimiter(clock);

        for (var i = 0; i < 3; i++)
            Assert.True(limiter.TryAcquire("u", 3, TimeSpan.FromSeconds(10)));

        Assert.False(limiter.TryAcquire("u", 3, TimeSpan.FromSeconds(10)));

        clock.AdvanceSeconds(10.1);
        Assert.True(limiter.TryAcquire("u", 3, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void RateLimiterIsPerKey()
    {
        var clock = new FakeClock();
        var limiter = new RateLimiter(clock);

        Assert.True(limiter.TryAcquire("a", 1, TimeSpan.FromSeconds(10)));
        Assert.False(limiter.TryAcquire("a", 1, TimeSpan.FromSeconds(10)));
        Assert.True(limiter.TryAcquire("b", 1, TimeSpan.FromSeconds(10)));
    }
}
