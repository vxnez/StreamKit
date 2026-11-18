using StreamKit.Core.Abstractions;
using StreamKit.Core.Events;
using StreamKit.Core.Logging;
using StreamKit.Core.Options;
using StreamKit.TikTok;
using Xunit;

namespace StreamKit.Tests;

public class TikTokTests
{
    private static readonly DateTimeOffset At = new(2026, 3, 1, 20, 0, 0, TimeSpan.Zero);
    private static readonly TikTokUser Ana = new(42, "ana_plays", "Ana");

    // ---------- mapper ----------

    [Fact]
    public void ChatTrimsTextAndKeepsViewer()
    {
        var chat = TikTokEventMapper.MapChat(new TikTokChatMessage(Ana, "  hola  "), At);

        Assert.NotNull(chat);
        Assert.Equal("hola", chat!.Message);
        Assert.Equal("42", chat.Viewer.UserId);
        Assert.Equal("ana_plays", chat.Viewer.UniqueId);
        Assert.Equal("Ana", chat.Viewer.Nickname);
        Assert.Equal("chat", chat.Kind);
    }

    [Fact]
    public void ChatWithoutUserOrTextIsDiscarded()
    {
        Assert.Null(TikTokEventMapper.MapChat(new TikTokChatMessage(TikTokUser.Empty, "hola"), At));
        Assert.Null(TikTokEventMapper.MapChat(new TikTokChatMessage(Ana, "   "), At));
    }

    [Fact]
    public void GiftUsesComboCountAndUnitDiamonds()
    {
        var gift = TikTokEventMapper.MapGift(
            new TikTokGiftMessage(Ana, 5655, "Rosa", UnitDiamonds: 1, ComboCount: 7, StreakFinished: true),
            At);

        Assert.NotNull(gift);
        Assert.Equal("5655", gift!.GiftId);
        Assert.Equal("Rosa", gift.GiftName);
        Assert.Equal(7, gift.Count);
        Assert.Equal(7, gift.TotalDiamonds);
        Assert.True(gift.StreakFinished);
    }

    [Fact]
    public void GiftClampsAndFallsBackToName()
    {
        var gift = TikTokEventMapper.MapGift(
            new TikTokGiftMessage(Ana, 0, "  ", UnitDiamonds: -5, ComboCount: 0, StreakFinished: false),
            At);

        Assert.NotNull(gift);
        Assert.Equal("Regalo", gift!.GiftName);
        Assert.Equal("Regalo", gift.GiftId);
        Assert.Equal(1, gift.Count);
        Assert.Equal(0, gift.TotalDiamonds);
        Assert.False(gift.StreakFinished);
    }

    [Fact]
    public void GiftForUnknownUserIsDiscarded()
    {
        Assert.Null(TikTokEventMapper.MapGift(
            new TikTokGiftMessage(TikTokUser.Empty, 1, "Rosa", 1, 1, true),
            At));
    }

    [Fact]
    public void LikeClampsToAtLeastOne()
    {
        var like = TikTokEventMapper.MapLike(new TikTokLikeMessage(Ana, 0), At);

        Assert.NotNull(like);
        Assert.Equal(1, like!.Count);
    }

    [Fact]
    public void SocialEventsNeedAViewer()
    {
        Assert.Null(TikTokEventMapper.MapFollow(new TikTokFollowMessage(TikTokUser.Empty), At));
        Assert.Null(TikTokEventMapper.MapShare(new TikTokShareMessage(TikTokUser.Empty), At));
        Assert.Null(TikTokEventMapper.MapJoin(new TikTokJoinMessage(TikTokUser.Empty, 3), At));
        Assert.Null(TikTokEventMapper.MapSubscribe(new TikTokSubscribeMessage(TikTokUser.Empty, 1), At));

        Assert.IsType<FollowEvent>(TikTokEventMapper.MapFollow(new TikTokFollowMessage(Ana), At));
        Assert.IsType<ShareEvent>(TikTokEventMapper.MapShare(new TikTokShareMessage(Ana), At));
        Assert.IsType<JoinEvent>(TikTokEventMapper.MapJoin(new TikTokJoinMessage(Ana, 3), At));
    }

    [Fact]
    public void SubscribeClampsMonths()
    {
        var low = TikTokEventMapper.MapSubscribe(new TikTokSubscribeMessage(Ana, 0), At);
        var high = TikTokEventMapper.MapSubscribe(new TikTokSubscribeMessage(Ana, 500), At);

        Assert.Equal(1, low!.Months);
        Assert.Equal(60, high!.Months);
    }

    [Fact]
    public void ViewerCountClampsToRange()
    {
        Assert.Equal(0, TikTokEventMapper.MapViewers(new TikTokViewerCountMessage(-10), At).Count);
        Assert.Equal(10_000_000, TikTokEventMapper.MapViewers(new TikTokViewerCountMessage(50_000_000), At).Count);
    }

    [Fact]
    public void StreamEndedIsItsOwnKind()
        => Assert.Equal("streamEnded", TikTokEventMapper.MapStreamEnded(At).Kind);

    [Fact]
    public void UserFallsBackToHandleWhenIdIsMissing()
    {
        var viewer = new TikTokUser(0, " @ana_plays ", " ").ToViewer();

        Assert.Equal("ana_plays", viewer.UserId);
        Assert.Equal("ana_plays", viewer.UniqueId);
        Assert.Equal("ana_plays", viewer.DisplayName);
    }

    [Fact]
    public void UserWithoutIdOrHandleIsEmpty()
    {
        Assert.True(new TikTokUser(0, "  ", "Ana").ToViewer().IsEmpty);
        Assert.True(TikTokUser.Empty.ToViewer().IsEmpty);
    }

    // ---------- host normalisation ----------

    [Theory]
    [InlineData("@ana_plays", "ana_plays")]
    [InlineData("  Ana_Plays  ", "Ana_Plays")]
    [InlineData("https://www.tiktok.com/@ana_plays/live", "ana_plays")]
    [InlineData("https://www.tiktok.com/@ana_plays/live?lang=es", "ana_plays")]
    [InlineData("tiktok.com/@ana_plays", "ana_plays")]
    [InlineData("", "")]
    public void HostIdAcceptsHandlesAndUrls(string input, string expected)
        => Assert.Equal(expected, TikTokEventSource.NormalizeHostId(input));

    [Fact]
    public void SourceRejectsBlankHostBeforeTouchingTheNetwork()
    {
        var source = new TikTokEventSource(new TikTokOptions(), new FakeClock());

        Assert.Equal("tiktok", source.Name);
        Assert.Throws<ArgumentException>(() => { _ = source.StartAsync("  ", CancellationToken.None); });
    }

    [Fact]
    public async Task DisposedSourceRefusesToStart()
    {
        var source = new TikTokEventSource(new TikTokOptions(), new FakeClock());
        await source.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => { _ = source.StartAsync("@ana", CancellationToken.None); });
        Assert.Equal(SourceState.Disconnected, source.State);
    }

    // ---------- simulator ----------

    [Fact]
    public void SimulatorCyclesThroughEveryEventKind()
    {
        var simulator = new SimulatedEventSource(new TikTokOptions(), new FakeClock(At), NullAppLog.Instance, seed: 7);
        var received = new List<StreamEvent>();
        simulator.Received += received.Add;

        for (var i = 0; i < 8; i++)
            Assert.NotNull(simulator.EmitNext());

        Assert.Equal(
            new[] { "chat", "gift", "like", "follow", "share", "join", "subscribe", "viewers" },
            received.Select(e => e.Kind));

        Assert.All(received, e => Assert.Equal(At, e.At));

        var gift = Assert.IsType<GiftEvent>(received[1]);
        Assert.True(gift.Count >= 1);
        Assert.True(gift.Diamonds >= 0);
        Assert.True(gift.StreakFinished);

        var viewers = Assert.IsType<ViewerCountEvent>(received[7]);
        Assert.InRange(viewers.Count, 0, 10_000_000);
    }

    [Fact]
    public void SimulatorIsDeterministicWithASeed()
    {
        var first = new SimulatedEventSource(new TikTokOptions(), new FakeClock(), NullAppLog.Instance, seed: 123);
        var second = new SimulatedEventSource(new TikTokOptions(), new FakeClock(), NullAppLog.Instance, seed: 123);

        for (var i = 0; i < 24; i++)
        {
            var a = first.EmitNext();
            var b = second.EmitNext();
            Assert.Equal(a, b);
        }
    }

    [Fact]
    public void ThrowingSubscriberDoesNotBreakTheSimulator()
    {
        var simulator = new SimulatedEventSource(new TikTokOptions(), new FakeClock(), NullAppLog.Instance, seed: 1);
        simulator.Received += _ => throw new InvalidOperationException("boom");

        var evt = simulator.EmitNext();

        Assert.NotNull(evt);
    }

    [Fact]
    public async Task SimulatorStartsEmitsAndStops()
    {
        var options = new StreamKitOptions();
        options.TikTok.SimulatorEventsPerMinute = 6_000;

        var clock = new FakeClock();
        var simulator = new SimulatedEventSource(options.TikTok, clock, NullAppLog.Instance, seed: 3);
        var states = new List<SourceState>();
        simulator.StateChanged += (state, _) => states.Add(state);

        var firstEvent = new TaskCompletionSource<StreamEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        simulator.Received += evt => firstEvent.TrySetResult(evt);

        await simulator.StartAsync("@ana", CancellationToken.None);

        Assert.Equal(SourceState.Connected, simulator.State);
        var received = await firstEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(string.IsNullOrWhiteSpace(received.Kind));

        await simulator.StopAsync(CancellationToken.None);

        Assert.Equal(SourceState.Disconnected, simulator.State);
        Assert.Equal(
            new[] { SourceState.Connecting, SourceState.Connected, SourceState.Disconnected },
            states);
    }

    [Fact]
    public async Task SimulatorCannotStartTwice()
    {
        var options = new StreamKitOptions();
        options.TikTok.SimulatorEventsPerMinute = 1;

        var simulator = new SimulatedEventSource(options.TikTok, new FakeClock(), NullAppLog.Instance, seed: 1);
        await simulator.StartAsync("@ana", CancellationToken.None);

        try
        {
            Assert.Throws<InvalidOperationException>(() => { _ = simulator.StartAsync("@ana", CancellationToken.None); });
        }
        finally
        {
            await simulator.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task DisposedSimulatorRefusesToStart()
    {
        var simulator = new SimulatedEventSource(new TikTokOptions(), new FakeClock(), NullAppLog.Instance);
        await simulator.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => { _ = simulator.StartAsync("@ana", CancellationToken.None); });
    }
}
