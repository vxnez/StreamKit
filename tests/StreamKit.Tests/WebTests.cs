using System.Diagnostics;
using System.Text.Json;
using StreamKit.Core.Alerts;
using StreamKit.Core.Logging;
using StreamKit.Core.Options;
using StreamKit.Core.State;
using StreamKit.Web;
using Xunit;

namespace StreamKit.Tests;

public sealed class LocalRequestGuardTests
{
    private const int Port = 8787;
    private const string Token = "token-de-prueba-123";

    private static readonly LocalRequestGuard Guard = new(Token, Port);

    [Fact]
    public void Allows_loopback_host_with_token()
    {
        var decision = Guard.Check(new GuardRequest("127.0.0.1:8787", null, Token, "127.0.0.1", true));

        Assert.Equal(GuardDecision.Allow, decision);
    }

    [Fact]
    public void Allows_localhost_and_ipv6_hosts()
    {
        Assert.Equal(GuardDecision.Allow,
            Guard.Check(new GuardRequest("localhost:8787", null, Token, "::1", true)));

        Assert.Equal(GuardDecision.Allow,
            Guard.Check(new GuardRequest("[::1]:8787", null, Token, "::1", true)));
    }

    [Fact]
    public void Allows_https_loopback_origin()
    {
        var decision = Guard.Check(new GuardRequest(
            "127.0.0.1:8787",
            "https://localhost:8787",
            Token,
            "127.0.0.1",
            true));

        Assert.Equal(GuardDecision.Allow, decision);
    }

    [Fact]
    public void Allows_static_route_without_token()
    {
        var decision = Guard.Check(new GuardRequest("127.0.0.1:8787", null, null, "127.0.0.1", false));

        Assert.Equal(GuardDecision.Allow, decision);
    }

    [Theory]
    [InlineData("evil.example:8787")]
    [InlineData("192.168.1.10:8787")]
    [InlineData("127.0.0.1:9999")]
    [InlineData("127.0.0.1:")]
    [InlineData("[::1]:9999")]
    [InlineData("")]
    public void Rejects_foreign_or_mismatched_host(string host)
    {
        var decision = Guard.Check(new GuardRequest(host, null, Token, "127.0.0.1", true));

        Assert.Equal(GuardDecision.Forbidden, decision);
    }

    [Theory]
    [InlineData("http://evil.example")]
    [InlineData("http://127.0.0.1:9999")]
    [InlineData("https://192.168.1.10:8787")]
    [InlineData("file://127.0.0.1")]
    [InlineData("no-es-una-url")]
    public void Rejects_foreign_origin(string origin)
    {
        var decision = Guard.Check(new GuardRequest("127.0.0.1:8787", origin, Token, "127.0.0.1", true));

        Assert.Equal(GuardDecision.Forbidden, decision);
    }

    [Fact]
    public void Rejects_non_loopback_remote_address()
    {
        var decision = Guard.Check(new GuardRequest("127.0.0.1:8787", null, Token, "192.168.1.10", true));

        Assert.Equal(GuardDecision.Forbidden, decision);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("otro-token")]
    [InlineData("TOKEN-DE-PRUEBA-123")]
    [InlineData("token-de-prueba-1234")]
    public void Rejects_missing_or_wrong_token(string? token)
    {
        var decision = Guard.Check(new GuardRequest("127.0.0.1:8787", null, token, "127.0.0.1", true));

        Assert.Equal(GuardDecision.Unauthorized, decision);
    }

    [Fact]
    public void Foreign_origin_wins_over_missing_token()
    {
        var decision = Guard.Check(new GuardRequest("127.0.0.1:8787", "http://evil.example", null, "127.0.0.1", true));

        Assert.Equal(GuardDecision.Forbidden, decision);
    }

    [Fact]
    public void Empty_token_is_rejected_at_construction()
    {
        Assert.Throws<ArgumentException>(() => new LocalRequestGuard("   ", Port));
    }
}

public sealed class OverlayHubTests
{
    [Fact]
    public void Rejects_clients_beyond_the_limit()
    {
        var harness = HubHarness.Create(maxClients: 2);

        Assert.True(harness.Hub.TryRegister(new FakeOverlayConnection(harness.Hub, OverlayChannel.Alerts)));
        Assert.True(harness.Hub.TryRegister(new FakeOverlayConnection(harness.Hub, OverlayChannel.Chat)));
        Assert.False(harness.Hub.TryRegister(new FakeOverlayConnection(harness.Hub, OverlayChannel.Stats)));
        Assert.Equal(2, harness.Hub.ClientCount);
    }

    [Fact]
    public async Task Broadcast_reaches_only_the_matching_channel()
    {
        await using var harness = HubHarness.Create();
        var alerts = new FakeOverlayConnection(harness.Hub, OverlayChannel.Alerts);
        var chat = new FakeOverlayConnection(harness.Hub, OverlayChannel.Chat);
        harness.Hub.TryRegister(alerts);
        harness.Hub.TryRegister(chat);

        await harness.Hub.BroadcastAsync(OverlayChannel.Chat, """{"type":"chat"}""", CancellationToken.None);

        Assert.Equal(0, alerts.Count);
        Assert.Equal(1, chat.Count);
        Assert.Equal("chat", chat.Messages[0].Type);
    }

    [Fact]
    public async Task Enqueue_assigns_sequence_and_tracks_depth()
    {
        await using var harness = HubHarness.Create();
        var item = Alert("sin-seq", AlertKind.Custom, AlertPriority.Custom);

        harness.Hub.Enqueue(item);

        Assert.True(item.Sequence > 0);
        Assert.Equal(1, harness.Hub.QueueDepth);
    }

    [Fact]
    public async Task Pump_delivers_highest_priority_first()
    {
        await using var harness = HubHarness.Create();
        var overlay = new FakeOverlayConnection(harness.Hub, OverlayChannel.Alerts, autoAck: true);
        harness.Hub.TryRegister(overlay);

        harness.Hub.Enqueue(Alert("follow-1", AlertKind.Follow, AlertPriority.Follow));
        harness.Hub.Enqueue(Alert("gift-1", AlertKind.Gift, AlertPriority.Gift));
        harness.StartPump();

        await WaitUntilAsync(() => overlay.Count == 2);

        Assert.Equal(new[] { "gift-1", "follow-1" }, overlay.AlertIds);
        Assert.Equal(0, harness.Hub.QueueDepth);
        Assert.Null(harness.Hub.ActiveAlertId);
    }

    [Fact]
    public async Task Pump_waits_for_ack_before_next_alert()
    {
        await using var harness = HubHarness.Create();
        var overlay = new FakeOverlayConnection(harness.Hub, OverlayChannel.Alerts);
        harness.Hub.TryRegister(overlay);

        harness.Hub.Enqueue(Alert("a1", AlertKind.Gift, AlertPriority.Gift));
        harness.StartPump();

        await WaitUntilAsync(() => overlay.Count == 1);
        await Task.Delay(150);
        Assert.Single(overlay.Messages);
        Assert.Equal("a1", harness.Hub.ActiveAlertId);

        harness.Hub.Enqueue(Alert("a2", AlertKind.Follow, AlertPriority.Follow));
        harness.Hub.Ack("a1");

        await WaitUntilAsync(() => overlay.Count == 2);
        Assert.Equal(new[] { "a1", "a2" }, overlay.AlertIds);
    }

    [Fact]
    public async Task Pump_continues_after_ack_timeout()
    {
        await using var harness = HubHarness.Create(ackGraceMs: 0);
        var overlay = new FakeOverlayConnection(harness.Hub, OverlayChannel.Alerts);
        harness.Hub.TryRegister(overlay);

        harness.Hub.Enqueue(Alert("timeout-1", AlertKind.Gift, AlertPriority.Gift));
        harness.Hub.Enqueue(Alert("timeout-2", AlertKind.Follow, AlertPriority.Follow));

        var sw = Stopwatch.StartNew();
        harness.StartPump();

        await WaitUntilAsync(() => overlay.Count == 2);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds >= 400, $"Se rindió demasiado rápido: {sw.ElapsedMilliseconds} ms.");
        Assert.Equal(new[] { "timeout-1", "timeout-2" }, overlay.AlertIds);
    }

    [Fact]
    public async Task Disconnect_requeues_the_active_alert()
    {
        await using var harness = HubHarness.Create(holdSeconds: 5);
        var first = new FakeOverlayConnection(harness.Hub, OverlayChannel.Alerts);
        harness.Hub.TryRegister(first);

        harness.Hub.Enqueue(Alert("reintento", AlertKind.Gift, AlertPriority.Gift));
        harness.StartPump();

        await WaitUntilAsync(() => first.Count == 1);
        await harness.Hub.UnregisterAsync(first.Id);

        var second = new FakeOverlayConnection(harness.Hub, OverlayChannel.Alerts, autoAck: true);
        harness.Hub.TryRegister(second);

        await WaitUntilAsync(() => second.Count == 1);

        Assert.Equal("reintento", second.AlertIds[0]);
        Assert.Equal(1, harness.Hub.ClientCount);
    }

    [Fact]
    public async Task Alert_is_dropped_when_no_alert_overlay_is_connected()
    {
        await using var harness = HubHarness.Create(holdSeconds: 1);
        var chat = new FakeOverlayConnection(harness.Hub, OverlayChannel.Chat);
        harness.Hub.TryRegister(chat);

        harness.Hub.Enqueue(Alert("sin-cliente", AlertKind.Gift, AlertPriority.Gift));
        harness.StartPump();

        await WaitUntilAsync(() => harness.State.Snapshot().AlertsDropped == 1, timeoutMs: 5000);

        Assert.Equal(0, chat.Count);
        Assert.Equal(0, harness.Hub.QueueDepth);
        Assert.Null(harness.Hub.ActiveAlertId);
    }

    [Fact]
    public async Task Dispose_closes_every_connection()
    {
        var harness = HubHarness.Create();
        var first = new FakeOverlayConnection(harness.Hub, OverlayChannel.Alerts);
        var second = new FakeOverlayConnection(harness.Hub, OverlayChannel.Stats);
        harness.Hub.TryRegister(first);
        harness.Hub.TryRegister(second);

        await harness.DisposeAsync();

        Assert.NotNull(first.CloseReason);
        Assert.NotNull(second.CloseReason);
        Assert.Equal(0, harness.Hub.ClientCount);
    }

    private static AlertItem Alert(string id, AlertKind kind, int priority, int durationMs = 0) => new()
    {
        Id = id,
        Kind = kind,
        Priority = priority,
        Text = id,
        DurationMs = durationMs,
    };

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException($"La condición no se cumplió en {timeoutMs} ms.");

            await Task.Delay(10);
        }
    }
}

internal sealed class HubHarness : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private Task? _pump;

    private HubHarness(OverlayHub hub, RuntimeState state)
    {
        Hub = hub;
        State = state;
    }

    public OverlayHub Hub { get; }

    public RuntimeState State { get; }

    public static HubHarness Create(
        int maxClients = 16,
        int holdSeconds = 1,
        int ackGraceMs = 0,
        int maxDepth = 32)
    {
        var options = new StreamKitOptions();
        options.Alerts.MaxClients = maxClients;
        options.Alerts.NoClientHoldSeconds = holdSeconds;
        options.Alerts.AckGraceMs = ackGraceMs;
        options.Alerts.MaxDepth = maxDepth;

        var state = new RuntimeState();
        var hub = new OverlayHub(new AlertQueue(maxDepth), options, NullAppLog.Instance, state);
        return new HubHarness(hub, state);
    }

    public void StartPump() => _pump ??= Task.Run(() => Hub.RunPumpAsync(_cts.Token));

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();

        if (_pump is not null)
        {
            try
            {
                await _pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // esperado
            }
        }

        await Hub.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}

internal sealed class FakeOverlayConnection : IOverlayConnection
{
    private readonly OverlayHub _hub;
    private readonly bool _autoAck;
    private readonly object _gate = new();
    private readonly List<SentOverlayMessage> _messages = new();

    public FakeOverlayConnection(OverlayHub hub, OverlayChannel channel, bool autoAck = false)
    {
        _hub = hub;
        Channel = channel;
        _autoAck = autoAck;
    }

    public Guid Id { get; } = Guid.NewGuid();

    public OverlayChannel Channel { get; }

    public string? CloseReason { get; private set; }

    public int Count
    {
        get
        {
            lock (_gate)
                return _messages.Count;
        }
    }

    public IReadOnlyList<SentOverlayMessage> Messages
    {
        get
        {
            lock (_gate)
                return _messages.ToArray();
        }
    }

    public string[] AlertIds => Messages
        .Where(static m => m.Type == "alert")
        .Select(static m => m.Id!)
        .ToArray();

    public ValueTask SendAsync(string json, CancellationToken ct)
    {
        var message = SentOverlayMessage.Parse(json);

        lock (_gate)
            _messages.Add(message);

        if (_autoAck && message.Type == "alert" && message.Id is { Length: > 0 } id)
            _hub.Ack(id);

        return ValueTask.CompletedTask;
    }

    public ValueTask CloseAsync(string reason, CancellationToken ct)
    {
        CloseReason = reason;
        return ValueTask.CompletedTask;
    }
}

internal sealed record SentOverlayMessage(string Type, string? Id, string? Channel)
{
    public static SentOverlayMessage Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() ?? "" : "";
        var id = root.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
        var channel = root.TryGetProperty("channel", out var channelElement) ? channelElement.GetString() : null;

        return new SentOverlayMessage(type, id, channel);
    }
}
