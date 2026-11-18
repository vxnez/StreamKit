using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using StreamKit.Core.Abstractions;
using StreamKit.Core.Commands;
using StreamKit.Core.Diagnostics;
using StreamKit.Core.Events;
using StreamKit.Core.Logging;
using StreamKit.Core.Options;
using StreamKit.Core.State;

namespace StreamKit.Web;

public sealed record WebHostDependencies(
    StreamKitOptions Options,
    OverlayHub Hub,
    RuntimeState State,
    ResourceSampler Sampler,
    IViewerStore Store,
    IObsController? Obs,
    Func<StreamEvent, CancellationToken, Task> InjectEvent,
    IAppLog Log);

/// <summary>
/// Kestrel embebido, solo loopback. Sirve los overlays, expone una API mínima de control y
/// mantiene los WebSockets de OBS. No hay HTTPS: el tráfico nunca sale de la máquina.
/// </summary>
public sealed class WebHost : IAsyncDisposable
{
    private const string SecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; " +
        "connect-src 'self' ws://127.0.0.1:* ws://localhost:*; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    private readonly WebApplication _app;

    private WebHost(WebApplication app, string baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl;
    }

    public string BaseUrl { get; }

    public static async Task<WebHost> StartAsync(WebHostDependencies deps, CancellationToken ct)
    {
        var options = deps.Options;
        var guard = new LocalRequestGuard(options.Web.Token, options.Web.Port);
        var rateLimiter = new RateLimiter(SystemClock.Instance);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = Math.Max(16, options.Web.MaxBodyKb) * 1024L;
            kestrel.Limits.MaxRequestLineSize = 8 * 1024;
            kestrel.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
            kestrel.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
            kestrel.Listen(IPAddress.Loopback, options.Web.Port);
        });

        var app = builder.Build();

        app.Use(async (ctx, next) =>
        {
            var isApi = ctx.Request.Path.StartsWithSegments("/api");
            var isWebSocket = ctx.Request.Path.StartsWithSegments("/ws");
            var token = isWebSocket
                ? ctx.Request.Query["token"].ToString()
                : ctx.Request.Headers["X-StreamKit-Token"].ToString();

            var decision = guard.Check(new GuardRequest(
                Host: ctx.Request.Host.Value,
                Origin: ctx.Request.Headers["Origin"].ToString(),
                Token: token,
                RemoteIp: ctx.Connection.RemoteIpAddress?.ToString(),
                RequiresToken: isApi || isWebSocket));

            if (decision == GuardDecision.Forbidden)
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                await ctx.Response.WriteAsync("Origen no permitido.", ctx.RequestAborted);
                return;
            }

            if (decision == GuardDecision.Unauthorized)
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                await ctx.Response.WriteAsync("Token requerido.", ctx.RequestAborted);
                return;
            }

            if (isApi && !rateLimiter.TryAcquire($"api:{ctx.Connection.RemoteIpAddress}", 300, TimeSpan.FromSeconds(10)))
            {
                ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                return;
            }

            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
            ctx.Response.Headers["X-Frame-Options"] = "DENY";
            ctx.Response.Headers["Content-Security-Policy"] = SecurityPolicy;

            await next();
        });

        app.UseWebSockets(new Microsoft.AspNetCore.Builder.WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(20),
        });

        var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (Directory.Exists(webRoot))
        {
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(webRoot),
                OnPrepareResponse = static file =>
                {
                    file.Context.Response.Headers.CacheControl = "no-cache";
                },
            });
        }

        MapEndpoints(app, deps, guard);

        await app.StartAsync(ct).ConfigureAwait(false);

        deps.Log.Info("web", $"Escuchando en http://127.0.0.1:{options.Web.Port} (overlays en /overlay/).");
        return new WebHost(app, $"http://127.0.0.1:{options.Web.Port}");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _app.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // apagado forzado
        }

        await _app.DisposeAsync().ConfigureAwait(false);
    }

    private static void MapEndpoints(WebApplication app, WebHostDependencies deps, LocalRequestGuard guard)
    {
        app.MapGet("/health", () => Results.Json(new { ok = true, app = "StreamKit", protocol = 1 }));

        app.MapGet("/", () => Results.Content(IndexHtml, "text/html; charset=utf-8"));

        app.MapGet("/api/state", () =>
        {
            var snapshot = deps.State.Snapshot();
            var resources = deps.Sampler.Sample();

            return Results.Json(new
            {
                tiktok = new { state = snapshot.TikTokState, host = snapshot.TikTokHost, viewers = snapshot.Viewers },
                obs = new { connected = snapshot.ObsConnected, scene = snapshot.ObsScene },
                queue = new { depth = deps.Hub.QueueDepth, active = deps.Hub.ActiveAlertId },
                overlays = deps.Hub.ClientCount,
                counters = new
                {
                    events = snapshot.EventsProcessed,
                    dropped = snapshot.EventsDropped,
                    alertsDropped = snapshot.AlertsDropped,
                    commands = snapshot.CommandsHandled,
                },
                resources = new
                {
                    cpu = Math.Round(resources.CpuPercentOfCore, 1),
                    ramMb = Math.Round(resources.WorkingSetMb, 1),
                },
                uptimeSec = (long)snapshot.Uptime.TotalSeconds,
            });
        });

        app.MapGet("/api/diagnostics", () =>
        {
            var snapshot = deps.State.Snapshot();
            var resources = deps.Sampler.Sample();

            return Results.Json(new
            {
                resources,
                counters = new
                {
                    events = snapshot.EventsProcessed,
                    dropped = snapshot.EventsDropped,
                    alertsDropped = snapshot.AlertsDropped,
                    commands = snapshot.CommandsHandled,
                },
                gc = new
                {
                    heapMb = Math.Round(GC.GetTotalMemory(false) / 1024d / 1024d, 1),
                    gen0 = GC.CollectionCount(0),
                    gen1 = GC.CollectionCount(1),
                    gen2 = GC.CollectionCount(2),
                    pausedMs = GC.GetTotalPauseDuration().TotalMilliseconds,
                },
                threads = ThreadPool.ThreadCount,
                overlays = deps.Hub.ClientCount,
                uptimeSec = (long)snapshot.Uptime.TotalSeconds,
                port = guard.Port,
            });
        });

        app.MapGet("/api/viewers/top", async (HttpContext ctx) =>
        {
            var requested = int.TryParse(ctx.Request.Query["n"], out var n) ? n : 10;
            var top = await deps.Store.GetTopAsync(Math.Clamp(requested, 1, 50), ctx.RequestAborted);

            return Results.Json(top.Select(v => new
            {
                user = v.UniqueId,
                nickname = v.Nickname,
                points = v.Points,
                lifetime = v.LifetimePoints,
                gifts = v.Gifts,
                diamonds = v.Diamonds,
            }));
        });

        app.MapPost("/api/test/event", async (HttpContext ctx) =>
        {
            var maxBytes = Math.Max(16, deps.Options.Web.MaxBodyKb) * 1024;
            if (ctx.Request.ContentLength is { } length && length > maxBytes)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

            TestEventRequest? body;
            try
            {
                body = await ctx.Request.ReadFromJsonAsync<TestEventRequest>(ctx.RequestAborted);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "JSON inválido." });
            }

            if (body is null)
                return Results.BadRequest(new { error = "Cuerpo vacío." });

            var evt = BuildEvent(body, out var error);
            if (evt is null)
                return Results.BadRequest(new { error });

            await deps.InjectEvent(evt, ctx.RequestAborted);
            return Results.Ok(new { ok = true, kind = evt.Kind });
        });

        app.MapPost("/api/obs/scene", async (HttpContext ctx) =>
        {
            if (deps.Obs is null || !deps.Obs.IsConnected)
                return Results.Conflict(new { error = "OBS no está conectado." });

            var body = await ReadBodyAsync<ObsSceneRequest>(ctx);
            if (body is null || string.IsNullOrWhiteSpace(body.Scene) || body.Scene.Length > 128)
                return Results.BadRequest(new { error = "Escena inválida." });

            var ok = await deps.Obs.SetSceneAsync(body.Scene, ctx.RequestAborted);
            return Results.Json(new { ok }, statusCode: ok ? 200 : 502);
        });

        app.MapPost("/api/obs/filter", async (HttpContext ctx) =>
        {
            if (deps.Obs is null || !deps.Obs.IsConnected)
                return Results.Conflict(new { error = "OBS no está conectado." });

            var body = await ReadBodyAsync<ObsFilterRequest>(ctx);
            if (body is null
                || string.IsNullOrWhiteSpace(body.Source) || body.Source.Length > 128
                || string.IsNullOrWhiteSpace(body.Filter) || body.Filter.Length > 128)
            {
                return Results.BadRequest(new { error = "Fuente o filtro inválido." });
            }

            var ok = await deps.Obs.SetFilterEnabledAsync(body.Source, body.Filter, body.Enabled, ctx.RequestAborted);
            return Results.Json(new { ok }, statusCode: ok ? 200 : 502);
        });

        app.MapGet("/ws/overlay", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            var channel = OverlayMessages.ParseChannel(ctx.Request.Query["channel"]);
            if (channel is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsync("Canal inválido.", ctx.RequestAborted);
                return;
            }

            if (deps.Hub.ClientCount >= Math.Max(1, deps.Options.Alerts.MaxClients))
            {
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }

            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            var connection = new WebSocketOverlayConnection(socket, channel.Value);

            if (!deps.Hub.TryRegister(connection))
            {
                await socket.CloseAsync(
                    System.Net.WebSockets.WebSocketCloseStatus.PolicyViolation,
                    "límite de clientes",
                    CancellationToken.None);
                return;
            }

            try
            {
                await connection.SendAsync(
                    OverlayMessages.Hello(channel.Value, deps.Hub.QueueDepth),
                    ctx.RequestAborted);

                await connection.RunReceiveLoopAsync(deps.Hub, ctx.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                // cierre normal
            }
            catch (System.Net.WebSockets.WebSocketException)
            {
                // el cliente cortó
            }
            finally
            {
                try
                {
                    using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await connection.CloseAsync("fin del overlay", closeCts.Token);
                }
                catch
                {
                    // el cliente ya se había ido
                }

                await deps.Hub.UnregisterAsync(connection.Id);
            }
        });
    }

    private static async Task<T?> ReadBodyAsync<T>(HttpContext ctx) where T : class
    {
        try
        {
            return await ctx.Request.ReadFromJsonAsync<T>(ctx.RequestAborted);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static StreamEvent? BuildEvent(TestEventRequest body, out string? error)
    {
        error = null;

        var user = Clean(body.User, 64) ?? "sim_user";
        var nickname = Clean(body.Nickname, 64) ?? user;
        var viewer = new ViewerRef($"sim:{user}", user, nickname);
        var now = DateTimeOffset.UtcNow;

        switch ((body.Kind ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "chat":
            {
                var message = Clean(body.Message, 500);
                if (message is null)
                {
                    error = "Falta 'message'.";
                    return null;
                }

                return new ChatEvent(now, viewer, message, body.IsModerator, body.IsSubscriber, body.IsOwner);
            }

            case "gift":
                return new GiftEvent(
                    now,
                    viewer,
                    Clean(body.GiftId, 32) ?? "sim-gift",
                    Clean(body.Gift, 64) ?? "Regalo",
                    Math.Clamp(body.Count ?? 1, 1, 1000),
                    Math.Clamp(body.Diamonds ?? 1, 0, 1_000_000));

            case "follow":
                return new FollowEvent(now, viewer);

            case "share":
                return new ShareEvent(now, viewer);

            case "join":
                return new JoinEvent(now, viewer);

            case "like":
                return new LikeEvent(now, viewer, Math.Clamp(body.Count ?? 1, 1, 10_000));

            case "subscribe":
                return new SubscribeEvent(now, viewer, Math.Clamp(body.Count ?? 1, 1, 60));

            case "viewers":
                return new ViewerCountEvent(now, Math.Clamp(body.Count ?? 0, 0, 10_000_000));

            case "streamended":
                return new StreamEndedEvent(now);

            default:
                error = "kind debe ser chat|gift|follow|share|join|like|subscribe|viewers|streamEnded.";
                return null;
        }
    }

    private static string? Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private const string IndexHtml = """
        <!doctype html>
        <html lang="es">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>StreamKit</title>
          <link rel="stylesheet" href="/assets/overlay.css">
        </head>
        <body class="index-page">
          <main class="index-card">
            <h1>StreamKit</h1>
            <p>Servidor local de overlays. Copiá las URLs con token desde la ventana de StreamKit.</p>
            <ul>
              <li><a href="/overlay/alerts.html">Alertas</a></li>
              <li><a href="/overlay/chat.html">Chat</a></li>
              <li><a href="/overlay/stats.html">Estadísticas</a></li>
              <li><a href="/health">Health</a></li>
            </ul>
          </main>
        </body>
        </html>
        """;

    private sealed record TestEventRequest(
        string? Kind,
        string? User,
        string? Nickname,
        string? Message,
        string? Gift,
        string? GiftId,
        int? Count,
        int? Diamonds,
        bool IsModerator = false,
        bool IsSubscriber = false,
        bool IsOwner = false);

    private sealed record ObsSceneRequest(string? Scene);

    private sealed record ObsFilterRequest(string? Source, string? Filter, bool Enabled);
}

internal sealed class WebSocketOverlayConnection(WebSocket socket, OverlayChannel channel) : IOverlayConnection
{
    private const int MaxIncomingBytes = 8 * 1024;

    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public Guid Id { get; } = Guid.NewGuid();

    public OverlayChannel Channel { get; } = channel;

    public async ValueTask SendAsync(string json, CancellationToken ct)
    {
        if (socket.State != WebSocketState.Open)
            return;

        var payload = Encoding.UTF8.GetBytes(json);

        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async ValueTask CloseAsync(string reason, CancellationToken ct)
    {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, ct).ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                // ya cerrado
            }
        }
    }

    public async Task RunReceiveLoopAsync(OverlayHub hub, CancellationToken ct)
    {
        var buffer = new byte[MaxIncomingBytes];

        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);

            if (result.MessageType == WebSocketMessageType.Close)
                break;

            if (result.MessageType != WebSocketMessageType.Text || !result.EndOfMessage)
                continue;

            if (result.Count == 0 || result.Count > MaxIncomingBytes)
                continue;

            HandleClientMessage(hub, buffer.AsSpan(0, result.Count));
        }
    }

    private static void HandleClientMessage(OverlayHub hub, ReadOnlySpan<byte> payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload.ToArray());
            var root = document.RootElement;

            if (!root.TryGetProperty("type", out var typeElement))
                return;

            if (!string.Equals(typeElement.GetString(), "ack", StringComparison.OrdinalIgnoreCase))
                return;

            if (root.TryGetProperty("id", out var idElement) && idElement.GetString() is { Length: > 0 } id)
                hub.Ack(id);
        }
        catch (JsonException)
        {
            // mensaje inválido: se ignora
        }
    }
}
