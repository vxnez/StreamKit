using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using StreamKit.Core.Alerts;
using StreamKit.Core.Diagnostics;
using StreamKit.Core.Events;
using StreamKit.Core.Logging;
using StreamKit.Core.Options;
using StreamKit.Core.State;
using StreamKit.Web;
using Xunit;

namespace StreamKit.Tests;

/// <summary>Arranca el servidor real en un puerto efímero y lo ejercita por HTTP y WebSocket.</summary>
public sealed class WebHostTests : IAsyncLifetime
{
    private readonly string _token = "token-" + Guid.NewGuid().ToString("N");
    private readonly ConcurrentQueue<StreamEvent> _injected = new();

    private StreamKitOptions _options = null!;
    private OverlayHub _hub = null!;
    private ResourceSampler _sampler = null!;
    private WebHost _host = null!;
    private HttpClient _client = null!;
    private int _port;

    public async Task InitializeAsync()
    {
        _port = GetFreePort();

        _options = new StreamKitOptions();
        _options.Web.Port = _port;
        _options.Web.Token = _token;
        _options.Alerts.NoClientHoldSeconds = 1;

        var state = new RuntimeState();
        _hub = new OverlayHub(new AlertQueue(_options.Alerts.MaxDepth), _options, NullAppLog.Instance, state);

        _sampler = new ResourceSampler();
        _sampler.Start(TimeSpan.FromSeconds(1));

        _host = await WebHost.StartAsync(
            new WebHostDependencies(
                _options,
                _hub,
                state,
                _sampler,
                new InMemoryViewerStore(),
                Obs: null,
                InjectEvent: (evt, _) =>
                {
                    _injected.Enqueue(evt);
                    return Task.CompletedTask;
                },
                Log: NullAppLog.Instance),
            CancellationToken.None);

        _client = new HttpClient
        {
            BaseAddress = new Uri(_host.BaseUrl),
            Timeout = TimeSpan.FromSeconds(10),
        };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();

        if (_host is not null)
            await _host.DisposeAsync();

        if (_sampler is not null)
            await _sampler.DisposeAsync();

        if (_hub is not null)
            await _hub.DisposeAsync();
    }

    [Fact]
    public async Task Health_is_public()
    {
        using var response = await _client.GetAsync("/health");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("StreamKit", body);
        Assert.Contains("\"ok\":true", body);
    }

    [Fact]
    public async Task Api_requires_the_token()
    {
        using var response = await _client.GetAsync("/api/state");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Api_rejects_a_foreign_token()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/state");
        request.Headers.Add("X-StreamKit-Token", "no-es-el-token");
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Api_answers_with_the_token()
    {
        using var response = await _client.SendAsync(Authorized(HttpMethod.Get, "/api/state"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"queue\"", body);
        Assert.Contains("\"resources\"", body);
    }

    [Fact]
    public async Task Api_rejects_a_foreign_origin()
    {
        using var request = Authorized(HttpMethod.Get, "/api/state");
        request.Headers.Add("Origin", "http://evil.example");
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Api_rejects_a_foreign_host()
    {
        using var request = Authorized(HttpMethod.Get, "/api/state");
        request.Headers.Host = "evil.example";
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Overlay_assets_are_served_with_security_headers()
    {
        using var css = await _client.GetAsync("/assets/overlay.css");
        Assert.Equal(HttpStatusCode.OK, css.StatusCode);
        Assert.Equal("nosniff", css.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.True(css.Headers.Contains("Content-Security-Policy"));

        using var html = await _client.GetAsync("/overlay/alerts.html");
        Assert.Equal(HttpStatusCode.OK, html.StatusCode);
        Assert.Contains("overlay.js", await html.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Overlay_socket_receives_hello()
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(OverlayUri("alerts"), CancellationToken.None);

        var hello = await ReceiveTextAsync(socket);

        Assert.Contains("\"type\":\"hello\"", hello);
        Assert.Contains("\"channel\":\"alerts\"", hello);

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test", CancellationToken.None);
    }

    [Fact]
    public async Task Overlay_socket_requires_the_token()
    {
        using var socket = new ClientWebSocket();

        await Assert.ThrowsAsync<WebSocketException>(
            () => socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws/overlay?channel=alerts"), CancellationToken.None));
    }

    [Fact]
    public async Task Overlay_socket_rejects_an_unknown_channel()
    {
        using var socket = new ClientWebSocket();

        await Assert.ThrowsAsync<WebSocketException>(
            () => socket.ConnectAsync(OverlayUri("evil"), CancellationToken.None));
    }

    [Fact]
    public async Task Alert_reaches_the_overlay_socket()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pump = Task.Run(() => _hub.RunPumpAsync(cts.Token), CancellationToken.None);

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(OverlayUri("alerts"), cts.Token);
        _ = await ReceiveTextAsync(socket, cts.Token);

        _hub.Enqueue(new AlertItem
        {
            Id = "ws-1",
            Kind = AlertKind.Gift,
            Priority = AlertPriority.Gift,
            Text = "¡Regalo!",
            DurationMs = 1000,
        });

        var alert = await ReceiveTextAsync(socket, cts.Token);

        using var document = JsonDocument.Parse(alert);
        Assert.Equal("alert", document.RootElement.GetProperty("type").GetString());
        Assert.Equal("ws-1", document.RootElement.GetProperty("id").GetString());
        Assert.Equal("¡Regalo!", document.RootElement.GetProperty("text").GetString());

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test", cts.Token);
        await cts.CancelAsync();

        try
        {
            await pump;
        }
        catch (OperationCanceledException)
        {
            // esperado
        }
    }

    [Fact]
    public async Task Test_event_endpoint_injects_a_chat_event()
    {
        using var request = Authorized(HttpMethod.Post, "/api/test/event");
        request.Content = JsonContent.Create(new
        {
            kind = "chat",
            user = "ana",
            nickname = "Ana",
            message = "!puntos",
        });

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(_injected.TryDequeue(out var evt));

        var chat = Assert.IsType<ChatEvent>(evt);
        Assert.Equal("!puntos", chat.Message);
        Assert.Equal("ana", chat.Viewer.UniqueId);
    }

    [Fact]
    public async Task Test_event_endpoint_rejects_an_unknown_kind()
    {
        using var request = Authorized(HttpMethod.Post, "/api/test/event");
        request.Content = JsonContent.Create(new { kind = "hack" });

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_injected);
    }

    private HttpRequestMessage Authorized(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-StreamKit-Token", _token);
        return request;
    }

    private Uri OverlayUri(string channel)
        => new($"ws://127.0.0.1:{_port}/ws/overlay?channel={channel}&token={Uri.EscapeDataString(_token)}");

    private static async Task<string> ReceiveTextAsync(ClientWebSocket socket, CancellationToken ct = default)
    {
        var buffer = new byte[16 * 1024];
        using var stream = new MemoryStream();

        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, ct);

            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("El servidor cerró el socket.");

            stream.Write(buffer, 0, result.Count);

            if (result.EndOfMessage)
                return Encoding.UTF8.GetString(stream.ToArray());
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
