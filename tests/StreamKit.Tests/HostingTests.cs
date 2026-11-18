using StreamKit.Core.Alerts;
using StreamKit.Core.Commands;
using StreamKit.Core.Events;
using StreamKit.Core.Hosting;
using StreamKit.Core.Options;
using StreamKit.Core.Points;
using StreamKit.Core.State;
using Xunit;

namespace StreamKit.Tests;

public sealed class LiveAlertsTests
{
    private static readonly ViewerRef Ana = new("100", "ana", "Ana");

    [Fact]
    public void GiftAlert_exposes_the_payload_overlay_js_reads()
    {
        var options = new StreamKitOptions();
        var gift = new GiftEvent(DateTimeOffset.UtcNow, Ana, "5655", "Rosa", Count: 3, Diamonds: 5);

        var alert = LiveAlerts.From(gift, options);

        Assert.NotNull(alert);
        Assert.Equal(AlertKind.Gift, alert!.Kind);
        Assert.Equal(3, alert.Count);
        Assert.Equal("Ana envió 3 × Rosa", alert.Text);
        Assert.Equal("Rosa", alert.Data["gift"]);
        Assert.Equal(3, alert.Data["count"]);
        Assert.Equal(15, alert.Data["diamonds"]);
        Assert.Equal("ana", alert.Data["user"]);
        Assert.Equal("Ana", alert.Data["nickname"]);
        Assert.Equal("gift:100:5655", alert.MergeKey);
    }

    [Fact]
    public void FollowAlert_carries_viewer_and_kind()
    {
        var alert = LiveAlerts.From(new FollowEvent(DateTimeOffset.UtcNow, Ana), new StreamKitOptions());

        Assert.NotNull(alert);
        Assert.Equal(AlertKind.Follow, alert!.Kind);
        Assert.Equal("Ana nuevo seguidor", alert.Text);
        Assert.Equal("ana", alert.Data["user"]);
        Assert.Equal("Ana", alert.Data["nickname"]);
        Assert.True(alert.DurationMs >= 500);
    }

    [Fact]
    public void SubscribeAlert_reports_months()
    {
        var alert = LiveAlerts.From(
            new SubscribeEvent(DateTimeOffset.UtcNow, Ana, Months: 4),
            new StreamKitOptions());

        Assert.NotNull(alert);
        Assert.Equal(AlertKind.Subscribe, alert!.Kind);
        Assert.Equal(4, alert.Data["months"]);
        Assert.Contains("4 meses", alert.Text);
    }

    [Fact]
    public void LikeAlert_uses_count()
    {
        var alert = LiveAlerts.From(new LikeEvent(DateTimeOffset.UtcNow, Ana, Count: 7), new StreamKitOptions());

        Assert.NotNull(alert);
        Assert.Equal(AlertKind.Like, alert!.Kind);
        Assert.Equal(7, alert.Count);
        Assert.Equal(7, alert.Data["count"]);
        Assert.Contains("7 likes", alert.Text);
    }

    [Fact]
    public void Events_without_alert_are_ignored()
    {
        var options = new StreamKitOptions();

        Assert.Null(LiveAlerts.From(new JoinEvent(DateTimeOffset.UtcNow, Ana), options));
        Assert.Null(LiveAlerts.From(new ViewerCountEvent(DateTimeOffset.UtcNow, 12), options));
        Assert.Null(LiveAlerts.From(new StreamEndedEvent(DateTimeOffset.UtcNow), options));
    }
}

public sealed class LiveSessionHandlerTests
{
    private static readonly ViewerRef Ana = new("100", "ana", "Ana");

    private static (LiveSessionHandler Handler, InMemoryViewerStore Store, RecordingOutput Output, FakeClock Clock, StreamKitOptions Options) Build()
    {
        var clock = new FakeClock();
        var store = new InMemoryViewerStore();
        var options = new StreamKitOptions();
        var points = new PointsEngine(store, new CooldownTracker(clock), clock, options);
        var router = new CommandRouter(
            store,
            points,
            new CooldownTracker(clock),
            new RateLimiter(clock),
            new RuntimeState(),
            options);
        var output = new RecordingOutput();
        var handler = new LiveSessionHandler(
            points,
            router,
            new GiftStreakAggregator(clock),
            new AlertCoalescer(clock, options),
            output,
            options);

        return (handler, store, output, clock, options);
    }

    [Fact]
    public async Task Gift_streak_awards_once_and_queues_a_single_alert()
    {
        var (handler, store, output, clock, options) = Build();
        options.Alerts.GiftCoalesceMs = 0;

        await handler.HandleAsync(Gift(clock, 1, finished: false), CancellationToken.None);
        await handler.HandleAsync(Gift(clock, 1, finished: false), CancellationToken.None);
        await handler.HandleAsync(Gift(clock, 2, finished: true), CancellationToken.None);

        Assert.Equal(20, await store.GetPointsAsync(Ana.UserId, CancellationToken.None));
        var alert = Assert.Single(output.Alerts);
        Assert.Equal(4, alert.Count);
        Assert.Equal("Rosa", alert.Data["gift"]);
    }

    [Fact]
    public async Task Stream_end_flushes_open_gift_streaks()
    {
        var (handler, store, output, clock, options) = Build();
        options.Alerts.GiftCoalesceMs = 0;

        await handler.HandleAsync(Gift(clock, 3, finished: false), CancellationToken.None);
        Assert.Equal(0, await store.GetPointsAsync(Ana.UserId, CancellationToken.None));

        await handler.HandleAsync(new StreamEndedEvent(clock.UtcNow), CancellationToken.None);

        Assert.Equal(15, await store.GetPointsAsync(Ana.UserId, CancellationToken.None));
        var alert = Assert.Single(output.Alerts);
        Assert.Equal(3, alert.Count);
    }

    [Fact]
    public async Task Chat_command_replies_on_the_overlay()
    {
        var (handler, store, output, clock, _) = Build();

        await handler.HandleAsync(
            new ChatEvent(clock.UtcNow, Ana, "!puntos"),
            CancellationToken.None);

        Assert.Single(output.Chats);
        var bot = Assert.Single(output.Bot);
        Assert.False(bot.IsError);
        Assert.Contains("tenés 1 puntos", bot.Text);
        Assert.Equal(1, await store.GetPointsAsync(Ana.UserId, CancellationToken.None));
    }

    [Fact]
    public async Task Like_burst_is_published_by_the_alert_tick()
    {
        var (handler, _, output, clock, options) = Build();
        options.Alerts.LikeCoalesceMs = 3000;

        await handler.HandleAsync(new LikeEvent(clock.UtcNow, Ana, Count: 1), CancellationToken.None);
        await handler.HandleAsync(new LikeEvent(clock.UtcNow, Ana, Count: 2), CancellationToken.None);
        Assert.Empty(output.Alerts);

        clock.AdvanceSeconds(3);
        handler.FlushAlerts();

        var alert = Assert.Single(output.Alerts);
        Assert.Equal(AlertKind.Like, alert.Kind);
        Assert.Equal(3, alert.Count);
    }

    private static GiftEvent Gift(FakeClock clock, int count, bool finished) => new(
        clock.UtcNow,
        Ana,
        "5655",
        "Rosa",
        count,
        Diamonds: 5,
        StreakFinished: finished);

    private sealed class RecordingOutput : ILiveOutput
    {
        public List<AlertItem> Alerts { get; } = new();
        public List<ChatEvent> Chats { get; } = new();
        public List<(string Text, bool IsError)> Bot { get; } = new();

        public void EnqueueAlert(AlertItem item) => Alerts.Add(item);

        public ValueTask ShowChatAsync(ChatEvent chat, CancellationToken ct)
        {
            Chats.Add(chat);
            return ValueTask.CompletedTask;
        }

        public ValueTask ShowBotMessageAsync(string text, bool isError, CancellationToken ct)
        {
            Bot.Add((text, isError));
            return ValueTask.CompletedTask;
        }
    }
}
