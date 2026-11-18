using StreamKit.Core.Abstractions;
using StreamKit.Core.Commands;
using StreamKit.Core.Events;
using StreamKit.Core.Options;
using StreamKit.Core.Points;
using Xunit;

namespace StreamKit.Tests;

public class PointsEngineTests
{
    private static readonly ViewerRef Ana = new("100", "ana", "Ana");

    private static (PointsEngine Engine, InMemoryViewerStore Store, FakeClock Clock, StreamKitOptions Options) Build()
    {
        var clock = new FakeClock();
        var store = new InMemoryViewerStore();
        var options = new StreamKitOptions();
        var engine = new PointsEngine(store, new CooldownTracker(clock), clock, options);
        return (engine, store, clock, options);
    }

    [Fact]
    public async Task GiftAwardsDiamondsTimesCount()
    {
        var (engine, store, _, _) = Build();
        var gift = new GiftEvent(DateTimeOffset.UtcNow, Ana, "5655", "Rosa", Count: 2, Diamonds: 5);

        var balance = await engine.AwardForGiftAsync(gift, CancellationToken.None);

        Assert.Equal(10, balance);
        Assert.Equal(10, await store.GetPointsAsync(Ana.UserId, CancellationToken.None));
    }

    [Fact]
    public async Task GiftWithZeroDiamondsStillAwardsPerUnit()
    {
        var (engine, _, _, _) = Build();
        var gift = new GiftEvent(DateTimeOffset.UtcNow, Ana, "0", "Desconocido", Count: 3, Diamonds: 0);

        Assert.Equal(3, await engine.AwardForGiftAsync(gift, CancellationToken.None));
    }

    [Fact]
    public async Task UnfinishedStreakDoesNotAward()
    {
        var (engine, store, _, _) = Build();
        var gift = new GiftEvent(DateTimeOffset.UtcNow, Ana, "5655", "Rosa", Count: 1, Diamonds: 5, StreakFinished: false);

        Assert.Equal(0, await engine.AwardForGiftAsync(gift, CancellationToken.None));
        Assert.Equal(0, await store.GetPointsAsync(Ana.UserId, CancellationToken.None));
    }

    [Fact]
    public async Task ChatAwardsOncePerWindow()
    {
        var (engine, store, clock, _) = Build();
        var chat = new ChatEvent(DateTimeOffset.UtcNow, Ana, "hola");

        Assert.Equal(1, await engine.AwardForChatAsync(chat, CancellationToken.None));
        Assert.Null(await engine.AwardForChatAsync(chat, CancellationToken.None));

        clock.AdvanceSeconds(61);
        Assert.Equal(2, await engine.AwardForChatAsync(chat, CancellationToken.None));
        Assert.Equal(2, await store.GetPointsAsync(Ana.UserId, CancellationToken.None));
    }

    [Fact]
    public async Task ChatAwardsNothingWhenDisabled()
    {
        var (engine, _, _, options) = Build();
        options.Points.PerChat = 0;

        Assert.Null(await engine.AwardForChatAsync(new ChatEvent(DateTimeOffset.UtcNow, Ana, "hola"), CancellationToken.None));
    }

    [Fact]
    public async Task WatchTimeAwardsAllActiveViewers()
    {
        var (engine, store, _, _) = Build();
        await store.TouchAsync(Ana, DateTimeOffset.UtcNow, CancellationToken.None);
        await store.TouchAsync(new ViewerRef("200", "beto", "Beto"), DateTimeOffset.UtcNow, CancellationToken.None);

        var count = await engine.AwardWatchTimeAsync(CancellationToken.None);

        Assert.Equal(2, count);
        Assert.Equal(1, await store.GetPointsAsync(Ana.UserId, CancellationToken.None));
    }

    [Fact]
    public async Task TransferMovesPointsBetweenViewers()
    {
        var (engine, store, _, _) = Build();
        var beto = new ViewerRef("200", "beto", "Beto");
        await store.AddPointsAsync(Ana, 100, "seed", DateTimeOffset.UtcNow, CancellationToken.None);
        await store.TouchAsync(beto, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.True(await engine.TransferAsync(Ana, "beto", 40, CancellationToken.None));
        Assert.Equal(60, await store.GetPointsAsync(Ana.UserId, CancellationToken.None));
        Assert.Equal(40, await store.GetPointsAsync(beto.UserId, CancellationToken.None));
    }

    [Fact]
    public async Task TransferFailsWithoutFunds()
    {
        var (engine, store, _, _) = Build();
        var beto = new ViewerRef("200", "beto", "Beto");
        await store.TouchAsync(Ana, DateTimeOffset.UtcNow, CancellationToken.None);
        await store.TouchAsync(beto, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.False(await engine.TransferAsync(Ana, "beto", 10, CancellationToken.None));
    }
}
