using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using StreamKit.Core.Abstractions;
using StreamKit.Core.Alerts;
using StreamKit.Core.Commands;
using StreamKit.Core.Diagnostics;
using StreamKit.Core.Events;
using StreamKit.Core.Hosting;
using StreamKit.Core.Logging;
using StreamKit.Core.Options;
using StreamKit.Core.Points;
using StreamKit.Core.State;
using StreamKit.Data;
using StreamKit.Web;

namespace StreamKit.Bench;

/// <summary>
/// Auditoría de recursos del proceso real: canal acotado, pipeline secuencial, SQLite y
/// WebSockets de overlay. Mide CPU/RAM del proceso y la latencia alerta→overlay con clientes
/// reales; no usa OBS ni TikTok.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        BenchConfig config;
        try
        {
            config = BenchConfig.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(BenchConfig.Usage);
            return 2;
        }

        var log = NullAppLog.Instance;
        var options = new StreamKitOptions
        {
            Web = { Port = config.Port, Token = Guid.NewGuid().ToString("N") },
            Alerts = { GiftCoalesceMs = 0, LikeCoalesceMs = 0 },
            Logging = { Level = AppLogLevel.Warn },
        };

        var directory = Path.Combine(
            Path.GetTempPath(),
            "StreamKitBench",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var state = new RuntimeState();
        var sampler = new ResourceSampler();
        sampler.Start(TimeSpan.FromSeconds(1));

        var database = new SqliteDatabase(Path.Combine(directory, "bench.db"));
        await database.InitializeAsync(CancellationToken.None);
        var store = new SqliteViewerStore(database, SystemClock.Instance, log);
        await store.LoadAsync(CancellationToken.None);
        store.StartAutoFlush(TimeSpan.FromSeconds(5));

        var hub = new OverlayHub(new AlertQueue(1024), options, log, state);
        var output = new CountingOutput(hub);
        var points = new PointsEngine(store, new CooldownTracker(SystemClock.Instance), SystemClock.Instance, options);
        var commands = new CommandRouter(
            store,
            points,
            new CooldownTracker(SystemClock.Instance),
            new RateLimiter(SystemClock.Instance),
            state,
            options);
        var live = new LiveSessionHandler(
            points,
            commands,
            new GiftStreakAggregator(SystemClock.Instance),
            new AlertCoalescer(SystemClock.Instance, options),
            output,
            options);

        var events = System.Threading.Channels.Channel.CreateBounded<StreamEvent>(
            new System.Threading.Channels.BoundedChannelOptions(4096)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });

        WebHost? web = null;
        var cts = new CancellationTokenSource();
        var clients = new List<BenchClient>();
        Task? pipeline = null;
        Task? pump = null;

        try
        {
            web = await WebHost.StartAsync(
                new WebHostDependencies(
                    options,
                    hub,
                    state,
                    sampler,
                    store,
                    Obs: null,
                    InjectEvent: (evt, _) =>
                    {
                        if (!events.Writer.TryWrite(evt))
                            state.MarkEventDropped();
                        return Task.CompletedTask;
                    },
                    log),
                cts.Token);

            var wsUrl = $"ws://127.0.0.1:{config.Port}/ws/overlay?channel=alerts&token={Uri.EscapeDataString(options.Web.Token)}";
            for (var i = 0; i < config.Clients; i++)
            {
                var client = new BenchClient();
                await client.ConnectAsync(wsUrl, cts.Token);
                clients.Add(client);
            }

            pipeline = Task.Run(() => new EventPipeline([live], state, log).RunAsync(events.Reader, cts.Token));
            pump = Task.Run(() => hub.RunPumpAsync(cts.Token));

            // Un instante para que Kestrel acepte los sockets antes de medir.
            await Task.Delay(300);

            var samples = new List<ResourceSnapshot>();
            var produced = 0;
            var totalSeconds = config.Seconds;

            Console.WriteLine("StreamKit bench");
            Console.WriteLine($"  duración     : {totalSeconds:0} s");
            Console.WriteLine($"  eventos/min  : {config.EventsPerMinute}");
            Console.WriteLine($"  overlays WS  : {config.Clients}");
            Console.WriteLine($"  ráfaga       : {config.Burst} alertas");
            Console.WriteLine($"  puerto       : {config.Port}");
            Console.WriteLine();

            var stopwatch = Stopwatch.StartNew();

            for (var i = 0; i < config.Burst; i++)
            {
                Write(events, state, NextEvent(ref produced, burst: true));
            }

            if (config.EventsPerMinute > 0)
            {
                var interval = TimeSpan.FromSeconds(60d / config.EventsPerMinute);
                if (interval < TimeSpan.FromMilliseconds(1))
                    interval = TimeSpan.FromMilliseconds(1);

                using var timer = new PeriodicTimer(interval);
                var nextSampleAt = TimeSpan.FromSeconds(1);

                while (stopwatch.Elapsed < TimeSpan.FromSeconds(totalSeconds)
                       && await timer.WaitForNextTickAsync(cts.Token))
                {
                    Write(events, state, NextEvent(ref produced, burst: false));

                    if (stopwatch.Elapsed >= nextSampleAt)
                    {
                        samples.Add(sampler.Sample());
                        nextSampleAt += TimeSpan.FromSeconds(1);
                    }
                }
            }
            else
            {
                while (stopwatch.Elapsed < TimeSpan.FromSeconds(totalSeconds))
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
                    samples.Add(sampler.Sample());
                }
            }

            stopwatch.Stop();

            // Deja drenar el canal y la cola de alertas antes de medir el cierre.
            var drain = Stopwatch.StartNew();
            while (drain.Elapsed < TimeSpan.FromSeconds(10))
            {
                var snapshot = state.Snapshot();
                if (snapshot.EventsProcessed + snapshot.EventsDropped >= produced && hub.QueueDepth == 0)
                    break;

                await Task.Delay(100);
            }

            await Task.Delay(300);

            var final = state.Snapshot();
            samples.Add(sampler.Sample());

            var latencies = clients.Count > 0
                ? clients.OrderByDescending(c => c.Latencies.Count).First().Latencies.ToArray()
                : Array.Empty<double>();

            var seconds = Math.Max(1, stopwatch.Elapsed.TotalSeconds);

            // Las primeras muestras incluyen JIT, arranque de Kestrel y creación de la base:
            // se excluyen para reportar el régimen estable, que es lo que ve el streamer.
            var steady = samples.Count > 3 ? samples.Skip(3).ToArray() : samples.ToArray();
            var cpu = steady.Select(s => s.CpuPercentOfCore).ToArray();
            var rss = samples.Select(s => s.WorkingSetMb).ToArray();

            Console.WriteLine($"  eventos      : {produced:N0} generados · {final.EventsProcessed:N0} procesados · "
                            + $"{final.EventsDropped:N0} descartados ({(produced / seconds) * 60:0}/min)");
            Console.WriteLine($"  alertas      : {output.Enqueued:N0} encoladas · {clients.Max(c => c.Received):N0} recibidas "
                            + $"· {final.AlertsDropped:N0} descartadas · {hub.ClientCount} clientes al cierre");
            Console.WriteLine($"  CPU          : media {Average(cpu):0.0} % de un núcleo · p95 {Percentile(cpu, 95):0.0} % "
                            + $"· máx {Max(cpu):0.0} % (régimen, sin arranque)");
            Console.WriteLine($"  RAM          : media {Average(rss):0.0} MB · máx {Max(rss):0.0} MB "
                            + $"· heap {samples[^1].ManagedHeapMb:0.0} MB");
            Console.WriteLine($"  latencia     : p50 {Percentile(latencies, 50):0} ms · p95 {Percentile(latencies, 95):0} ms "
                            + $"· p99 {Percentile(latencies, 99):0} ms · máx {Max(latencies):0} ms ({latencies.Length:N0} muestras)");
            Console.WriteLine($"  uptime       : {final.Uptime:hh\\:mm\\:ss}");

            return 0;
        }
        finally
        {
            await cts.CancelAsync().ConfigureAwait(false);

            foreach (var client in clients)
                await client.DisposeAsync().ConfigureAwait(false);

            if (pipeline is not null)
                await QuietlyAsync(pipeline).ConfigureAwait(false);
            if (pump is not null)
                await QuietlyAsync(pump).ConfigureAwait(false);

            events.Writer.TryComplete();

            if (web is not null)
                await web.DisposeAsync().ConfigureAwait(false);

            await hub.DisposeAsync().ConfigureAwait(false);
            await store.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            await store.DisposeAsync().ConfigureAwait(false);
            await database.DisposeAsync().ConfigureAwait(false);
            await sampler.DisposeAsync().ConfigureAwait(false);
            cts.Dispose();

            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // SQLite puede tardar en soltar el archivo; el temp se limpia en otra corrida.
            }
        }
    }

    private static void Write(System.Threading.Channels.Channel<StreamEvent> events, RuntimeState state, StreamEvent evt)
    {
        if (!events.Writer.TryWrite(evt))
            state.MarkEventDropped();
    }

    private static readonly (string User, string Nick)[] People =
    [
        ("ana_plays", "Ana"),
        ("bruno_mc", "Bruno"),
        ("carla.craft", "Carla"),
        ("dani_redstone", "Dani"),
        ("elena_builds", "Elena"),
        ("fede_gg", "Fede"),
    ];

    private static StreamEvent NextEvent(ref int index, bool burst)
    {
        var person = People[index % People.Length];
        var viewer = new ViewerRef($"bench:{person.User}", person.User, person.Nick);
        var now = DateTimeOffset.UtcNow;
        var slot = burst ? 3 : index % 10;
        index++;

        return slot switch
        {
            3 => new GiftEvent(now, viewer, "bench-gift", "Rosa", Count: 1, Diamonds: 10),
            4 => new LikeEvent(now, viewer, Count: 5),
            5 => new FollowEvent(now, viewer),
            6 => new ShareEvent(now, viewer),
            7 => new JoinEvent(now, viewer),
            8 => new ChatEvent(now, viewer, "hola stream"),
            _ => new ChatEvent(now, viewer, "gg"),
        };
    }

    private static double Average(double[] values) => values.Length == 0 ? 0 : values.Average();

    private static double Max(double[] values) => values.Length == 0 ? 0 : values.Max();

    private static double Percentile(double[] values, double percentile)
    {
        if (values.Length == 0)
            return 0;

        var sorted = values.OrderBy(v => v).ToArray();
        var index = (int)Math.Ceiling(percentile / 100d * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    private static async Task QuietlyAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // cierre normal
        }
        catch
        {
            // el bench no falla por un bucle de fondo
        }
    }

    private sealed class CountingOutput(OverlayHub hub) : ILiveOutput
    {
        private int _enqueued;

        public int Enqueued => Volatile.Read(ref _enqueued);

        public void EnqueueAlert(AlertItem item)
        {
            Interlocked.Increment(ref _enqueued);
            hub.Enqueue(item);
        }

        public ValueTask ShowChatAsync(ChatEvent chat, CancellationToken ct)
            => new(hub.BroadcastAsync(OverlayChannel.Chat, OverlayMessages.Chat(chat), ct));

        public ValueTask ShowBotMessageAsync(string text, bool isError, CancellationToken ct)
            => new(hub.BroadcastAsync(OverlayChannel.Chat, OverlayMessages.BotMessage(text, isError), ct));
    }

    private sealed class BenchClient : IAsyncDisposable
    {
        private readonly ClientWebSocket _socket = new();
        private readonly CancellationTokenSource _cts = new();
        private Task? _loop;
        private int _received;

        public ConcurrentQueue<double> Latencies { get; } = new();

        public int Received => Volatile.Read(ref _received);

        public async Task ConnectAsync(string url, CancellationToken ct)
        {
            await _socket.ConnectAsync(new Uri(url), ct).ConfigureAwait(false);
            _loop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            var buffer = new byte[16 * 1024];
            using var message = new MemoryStream();

            try
            {
                while (!ct.IsCancellationRequested && _socket.State == WebSocketState.Open)
                {
                    var result = await _socket.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return;

                    message.Write(buffer, 0, result.Count);
                    if (!result.EndOfMessage)
                        continue;

                    var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                    message.SetLength(0);
                    await HandleAsync(text).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // cierre normal
            }
            catch (WebSocketException)
            {
                // el servidor cerró
            }
        }

        private async Task HandleAsync(string text)
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                var root = document.RootElement;

                if (!root.TryGetProperty("type", out var type)
                    || !string.Equals(type.GetString(), "alert", StringComparison.Ordinal))
                {
                    return;
                }

                Interlocked.Increment(ref _received);

                if (root.TryGetProperty("ts", out var ts) && ts.TryGetInt64(out var millis))
                {
                    var sentAt = DateTimeOffset.FromUnixTimeMilliseconds(millis);
                    Latencies.Enqueue((DateTimeOffset.UtcNow - sentAt).TotalMilliseconds);
                }

                if (root.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } alertId)
                    await AckAsync(alertId).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                // mensaje ajeno al contrato: se ignora
            }
        }

        private async Task AckAsync(string alertId)
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes($"{{\"type\":\"ack\",\"id\":\"{alertId}\"}}");
                await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, _cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or InvalidOperationException or OperationCanceledException)
            {
                // el socket ya se cerró o se está cerrando
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync().ConfigureAwait(false);

            try
            {
                if (_socket.State == WebSocketState.Open)
                {
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "fin", CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            catch
            {
                // ya cerrado
            }

            if (_loop is not null)
                await QuietlyAsync(_loop).ConfigureAwait(false);

            _socket.Dispose();
            _cts.Dispose();
        }
    }

    private sealed record BenchConfig(int Seconds, int EventsPerMinute, int Clients, int Port, int Burst)
    {
        public const string Usage =
            "Uso: dotnet run --project tools/StreamKit.Bench -c Release -- "
            + "--seconds 60 --events-per-minute 500 --clients 5 --port 8799 --burst 0";

        public static BenchConfig Parse(string[] args)
        {
            var seconds = 60;
            var eventsPerMinute = 500;
            var clients = 5;
            var port = 8799;
            var burst = 0;

            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                string Next()
                {
                    if (i + 1 >= args.Length)
                        throw new ArgumentException($"Falta el valor de {arg}.");
                    return args[++i];
                }

                switch (arg)
                {
                    case "--seconds":
                        seconds = int.Parse(Next());
                        break;
                    case "--events-per-minute":
                        eventsPerMinute = int.Parse(Next());
                        break;
                    case "--clients":
                        clients = int.Parse(Next());
                        break;
                    case "--port":
                        port = int.Parse(Next());
                        break;
                    case "--burst":
                        burst = int.Parse(Next());
                        break;
                    case "--help":
                    case "-h":
                        throw new ArgumentException(Usage);
                    default:
                        throw new ArgumentException($"Argumento desconocido: {arg}");
                }
            }

            if (seconds is < 5 or > 3600)
                throw new ArgumentException("--seconds debe estar entre 5 y 3600.");
            if (eventsPerMinute is < 0 or > 6000)
                throw new ArgumentException("--events-per-minute debe estar entre 0 y 6000.");
            if (clients is < 1 or > 16)
                throw new ArgumentException("--clients debe estar entre 1 y 16.");
            if (port is < 1024 or > 65535)
                throw new ArgumentException("--port debe estar entre 1024 y 65535.");
            if (burst is < 0 or > 500)
                throw new ArgumentException("--burst debe estar entre 0 y 500.");

            return new BenchConfig(seconds, eventsPerMinute, clients, port, burst);
        }
    }
}
