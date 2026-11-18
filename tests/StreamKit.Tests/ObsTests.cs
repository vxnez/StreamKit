using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using StreamKit.Core.Abstractions;
using StreamKit.Core.Events;
using StreamKit.Core.Logging;
using StreamKit.Core.Rules;
using StreamKit.Obs;
using Xunit;

namespace StreamKit.Tests;

public sealed class ObsTests
{
    [Fact]
    public void Auth_vector_matches_obs_websocket_v5()
    {
        var actual = ObsAuth.CreateAuthenticationString(
            "supersecretpassword",
            "lM1GncleQOaCu9lT1yeUZhFYnqhsLLP1G5lAGo3ixaI=",
            "+IxH4CnCiqpX1rM9scsNynZzbOe4KhDeYcTNS3PDaeY=");

        Assert.Equal("1Ct943GAT+6YQUUX47Ia/ncufilbe6+oD6lY+5kaCu4=", actual);
    }

    [Fact]
    public void RuleMatcher_matches_kinds_filters_and_disabled_rules()
    {
        var viewer = new ViewerRef("42", "alice", "Alice");
        var gift = new GiftEvent(DateTimeOffset.UtcNow, viewer, "rose", "Rose", 3, 10);

        Assert.True(RuleMatcher.Matches(new AutomationRule { On = "gift", Where = new RuleWhere { GiftId = "rose", MinCount = 3, User = "@alice" } }, gift));
        Assert.False(RuleMatcher.Matches(new AutomationRule { On = "like" }, gift));
        Assert.False(RuleMatcher.Matches(new AutomationRule { Enabled = false }, gift));
        Assert.True(RuleMatcher.Matches(new AutomationRule { On = "any", Where = new RuleWhere { GiftName = "ros" } }, gift));

        var chat = new ChatEvent(DateTimeOffset.UtcNow, viewer, "!dance please");
        Assert.True(RuleMatcher.Matches(new AutomationRule { On = "chat", Where = new RuleWhere { Command = "dance", Keyword = "please" } }, chat));
        Assert.False(RuleMatcher.Matches(new AutomationRule { Where = new RuleWhere { Command = "jump" } }, chat));
    }

    [Fact]
    public async Task Automation_executes_actions_in_order_and_honors_cooldown()
    {
        var clock = new FakeClock();
        var fake = new FakeObsController();
        var rule = new AutomationRule
        {
            Id = "gift-rule",
            On = "gift",
            CooldownMs = 1000,
            Actions =
            {
                new() { Type = "scene", Scene = "Gaming" },
                new() { Type = "filter", Source = "Camera", Filter = "Blur", Enabled = true },
                new() { Type = "sourceVisibility", Scene = "Gaming", Source = "Alert", Visible = false },
                new() { Type = "hotkey", Hotkey = "OBS.test" },
                new() { Type = "mute", Source = "Mic", Muted = true },
                new() { Type = "raw", RequestType = "GetVersion", RequestData = new() },
            },
        };

        await using var automation = new ObsAutomation(new[] { rule }, fake, clock);
        var evt = new GiftEvent(clock.UtcNow, new ViewerRef("1", "viewer", "Viewer"), "1", "Rose", 1, 1);
        await automation.HandleAsync(evt, CancellationToken.None);
        await automation.HandleAsync(evt, CancellationToken.None);

        await fake.WaitForCountAsync(6);
        Assert.Equal(new[] { "scene", "filter", "sourceVisibility", "hotkey", "mute", "raw" }, fake.Calls);

        clock.Advance(TimeSpan.FromSeconds(1));
        await automation.HandleAsync(evt, CancellationToken.None);
        await fake.WaitForCountAsync(12);
    }

    [Fact]
    public async Task Automation_replaces_rules_without_restart()
    {
        var clock = new FakeClock();
        var fake = new FakeObsController();
        var follow = new FollowEvent(clock.UtcNow, new ViewerRef("1", "viewer", "Viewer"));

        await using var automation = new ObsAutomation([], fake, clock);

        await automation.HandleAsync(follow, CancellationToken.None);
        await Task.Delay(50);
        Assert.Empty(fake.Calls);

        automation.ReplaceRules(
        [
            new AutomationRule
            {
                Id = "follow-scene",
                On = "follow",
                Actions = { new() { Type = "scene", Scene = "Gaming" } },
            },
        ]);

        await automation.HandleAsync(follow, CancellationToken.None);
        await fake.WaitForCountAsync(1);
        Assert.Equal("scene", fake.Calls[0]);
    }

    [Fact]
    public async Task Client_and_controller_handle_v5_requests()
    {
        await using var server = await FakeObsServer.StartAsync(requireAuthentication: true);
        await using var client = new ObsWebSocketClient(server.Url, "secret", TimeSpan.FromSeconds(2));
        var controller = new ObsController(client, SystemClock.Instance);

        await client.ConnectAsync(CancellationToken.None);
        Assert.True(client.IsConnected);
        var snapshot = await controller.GetSnapshotAsync(CancellationToken.None);
        Assert.True(snapshot.Connected);
        Assert.Equal("Gaming", snapshot.CurrentScene);
        Assert.Equal(new[] { "Gaming", "BRB" }, snapshot.Scenes);
        Assert.True(await controller.SetSceneAsync("BRB", CancellationToken.None));
        Assert.True(await controller.SetFilterEnabledAsync("Camera", "Blur", true, CancellationToken.None));
        Assert.True(await controller.SetSourceVisibleAsync("Gaming", "Alert", false, CancellationToken.None));
        Assert.True(await controller.TriggerHotkeyAsync("OBS.test", CancellationToken.None));
        Assert.True(await controller.SetMuteAsync("Mic", true, CancellationToken.None));
        Assert.True(await controller.SendRawAsync("GetVersion", null, CancellationToken.None));
    }

    [Fact]
    public async Task Client_rejects_wrong_password()
    {
        await using var server = await FakeObsServer.StartAsync(requireAuthentication: true, rejectAuthentication: true);
        await using var client = new ObsWebSocketClient(server.Url, "wrong", TimeSpan.FromSeconds(2));

        await Assert.ThrowsAsync<ObsAuthenticationException>(() => client.ConnectAsync(CancellationToken.None));
    }

    private sealed class FakeObsController : IObsController
    {
        private readonly object _sync = new();
        private readonly List<string> _calls = new();
        private readonly TaskCompletionSource<bool> _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsConnected => true;
        public IReadOnlyList<string> Calls { get { lock (_sync) return _calls.ToArray(); } }

        public Task<ObsSnapshot> GetSnapshotAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> SetSceneAsync(string scene, CancellationToken ct) => RecordAsync("scene");
        public Task<bool> SetFilterEnabledAsync(string source, string filter, bool enabled, CancellationToken ct) => RecordAsync("filter");
        public Task<bool> SetSourceVisibleAsync(string scene, string source, bool visible, CancellationToken ct) => RecordAsync("sourceVisibility");
        public Task<bool> TriggerHotkeyAsync(string hotkey, CancellationToken ct) => RecordAsync("hotkey");
        public Task<bool> SetMuteAsync(string source, bool muted, CancellationToken ct) => RecordAsync("mute");
        public Task<bool> SendRawAsync(string requestType, IReadOnlyDictionary<string, object?>? data, CancellationToken ct) => RecordAsync("raw");

        public async Task WaitForCountAsync(int count)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (DateTime.UtcNow < deadline)
            {
                if (Calls.Count >= count)
                    return;
                await Task.Delay(10);
            }
            Assert.True(Calls.Count >= count, $"Esperaba {count} llamadas, hubo {Calls.Count}.");
        }

        private Task<bool> RecordAsync(string call)
        {
            lock (_sync)
                _calls.Add(call);
            _changed.TrySetResult(true);
            return Task.FromResult(true);
        }
    }

    private sealed class FakeObsServer : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly bool _requireAuthentication;
        private readonly bool _rejectAuthentication;

        private FakeObsServer(WebApplication app, bool requireAuthentication, bool rejectAuthentication, int port)
        {
            _app = app;
            _requireAuthentication = requireAuthentication;
            _rejectAuthentication = rejectAuthentication;
            Url = $"ws://127.0.0.1:{port}/ws";
        }

        public string Url { get; }

        public static async Task<FakeObsServer> StartAsync(bool requireAuthentication, bool rejectAuthentication = false)
        {
            var port = GetFreePort();
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
            var app = builder.Build();
            app.UseWebSockets();
            var server = new FakeObsServer(app, requireAuthentication, rejectAuthentication, port);
            app.Map("/ws", server.HandleAsync);
            await app.StartAsync();
            return server;
        }

        public async ValueTask DisposeAsync() => await _app.StopAsync();

        private async Task HandleAsync(HttpContext context)
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync("obswebsocket.json");
            await SendAsync(socket, new
            {
                op = 0,
                d = new
                {
                    rpcVersion = 1,
                    authentication = _requireAuthentication ? new { salt = "salt", challenge = "challenge" } : null,
                },
            });

            using var identify = await ReceiveAsync(socket);
            if (_rejectAuthentication)
            {
                await socket.CloseAsync((WebSocketCloseStatus)4009, "Authentication failed", CancellationToken.None);
                return;
            }

            await SendAsync(socket, new { op = 2, d = new { negotiatedRpcVersion = 1 } });
            while (socket.State == WebSocketState.Open)
            {
                using var request = await ReceiveAsync(socket);
                var data = request.RootElement.GetProperty("d");
                var requestType = data.GetProperty("requestType").GetString()!;
                var requestId = data.GetProperty("requestId").GetString()!;
                object? responseData = requestType switch
                {
                    "GetSceneList" => new { currentProgramSceneName = "Gaming", scenes = new[] { new { sceneName = "Gaming" }, new { sceneName = "BRB" } } },
                    "GetSceneItemId" => new { sceneItemId = 7 },
                    _ => null,
                };
                await SendAsync(socket, new
                {
                    op = 7,
                    d = new
                    {
                        requestType,
                        requestId,
                        requestStatus = new { result = true, code = 100 },
                        responseData,
                    },
                });
            }
        }

        private static async Task SendAsync(WebSocket socket, object value)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }

        private static async Task<JsonDocument> ReceiveAsync(WebSocket socket)
        {
            var buffer = new byte[16 * 1024];
            var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            return JsonDocument.Parse(buffer.AsMemory(0, result.Count));
        }

        private static int GetFreePort()
        {
            using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }
}
