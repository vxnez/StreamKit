using System.Security.Cryptography;
using System.Threading.Channels;
using StreamKit.Core.Abstractions;
using StreamKit.Core.Alerts;
using StreamKit.Core.Commands;
using StreamKit.Core.Diagnostics;
using StreamKit.Core.Events;
using StreamKit.Core.Hosting;
using StreamKit.Core.Logging;
using StreamKit.Core.Options;
using StreamKit.Core.Points;
using StreamKit.Core.Rules;
using StreamKit.Core.State;
using StreamKit.Data;
using StreamKit.Obs;
using StreamKit.TikTok;
using StreamKit.Web;

namespace StreamKit.App;

/// <summary>
/// Composición del proceso: un solo canal de eventos, un pipeline secuencial, Kestrel y SQLite.
/// La UI solo lee estado y llama a <see cref="StartAsync"/>/<see cref="StopAsync"/>; nunca toca
/// los módulos de infraestructura directamente.
/// </summary>
public sealed class AppComposition : IAsyncDisposable
{
    private const int EventQueueCapacity = 4096;
    private const int AlertTickMs = 250;
    private const int StatsTickSeconds = 3;
    private const int WatchTickSeconds = 60;

    private readonly ConfigStore _configStore;
    private readonly Channel<StreamEvent> _events;
    private readonly CancellationTokenSource _stopCts = new();
    private readonly List<Task> _loops = new();
    private readonly object _sync = new();
    private readonly IStreamEventSource _source;

    private Task? _pipeline;
    private bool _stopped;

    private AppComposition(
        StreamKitOptions options,
        ConfigStore configStore,
        RollingFileLogger log,
        RuntimeState state,
        ResourceSampler sampler,
        SqliteDatabase database,
        SqliteViewerStore viewers,
        OverlayHub overlays,
        WebHost web,
        IStreamEventSource source,
        ObsWebSocketClient obsClient,
        ObsController obs,
        ObsAutomation automation,
        LiveSessionHandler live,
        PointsEngine points,
        Channel<StreamEvent> events)
    {
        Options = options;
        _configStore = configStore;
        Log = log;
        State = state;
        Resources = sampler;
        Database = database;
        Viewers = viewers;
        Overlays = overlays;
        Web = web;
        _source = source;
        ObsClient = obsClient;
        Obs = obs;
        Automation = automation;
        Live = live;
        Points = points;
        _events = events;
    }

    public StreamKitOptions Options { get; }
    public RollingFileLogger Log { get; }
    public RuntimeState State { get; }
    public ResourceSampler Resources { get; }
    public SqliteDatabase Database { get; }
    public SqliteViewerStore Viewers { get; }
    public OverlayHub Overlays { get; }
    public WebHost Web { get; }
    public ObsWebSocketClient ObsClient { get; }
    public ObsController Obs { get; }
    public ObsAutomation Automation { get; }
    public LiveSessionHandler Live { get; }
    public PointsEngine Points { get; }

    public IStreamEventSource Source => _source;

    public bool IsRunning { get; private set; }

    public string OverlayUrl(OverlayChannel channel)
        => $"{Web.BaseUrl}/overlay/{OverlayMessages.ChannelName(channel)}.html?token={Uri.EscapeDataString(Options.Web.Token)}";

    public static async Task<AppComposition> CreateAsync(CancellationToken ct = default)
    {
        var configStore = new ConfigStore();
        var options = await configStore.LoadAsync(ct).ConfigureAwait(false);
        options.Rules ??= [];
        if (string.IsNullOrWhiteSpace(options.Web.Token))
        {
            options.Web.Token = CreateToken();
            await configStore.SaveAsync(options, ct).ConfigureAwait(false);
        }

        var clock = SystemClock.Instance;
        var log = new RollingFileLogger(options.Logging);
        var state = new RuntimeState();
        var sampler = new ResourceSampler();
        sampler.Start(TimeSpan.FromSeconds(1));

        var dataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StreamKit",
            "streamkit.db");
        var database = new SqliteDatabase(dataPath);
        await database.InitializeAsync(ct).ConfigureAwait(false);
        var viewers = new SqliteViewerStore(database, clock, log);
        await viewers.LoadAsync(ct).ConfigureAwait(false);
        viewers.StartAutoFlush(TimeSpan.FromSeconds(5));

        var overlays = new OverlayHub(new AlertQueue(options.Alerts.MaxDepth), options, log, state);
        var output = new OverlayOutput(overlays);
        var points = new PointsEngine(viewers, new CooldownTracker(clock), clock, options);
        var commands = new CommandRouter(
            viewers,
            points,
            new CooldownTracker(clock),
            new RateLimiter(clock),
            state,
            options);
        var live = new LiveSessionHandler(
            points,
            commands,
            new GiftStreakAggregator(clock),
            new AlertCoalescer(clock, options),
            output,
            options);

        var obsClient = new ObsWebSocketClient(
            options.Obs.Url,
            options.Obs.Password,
            TimeSpan.FromSeconds(Math.Clamp(options.Obs.RequestTimeoutSeconds, 1, 120)),
            log);
        var obs = new ObsController(obsClient, clock, log);
        var automation = new ObsAutomation(options.Rules, obs, clock, log);

        var events = Channel.CreateBounded<StreamEvent>(new BoundedChannelOptions(EventQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

        var source = CreateSource(options, clock, log);

        WebHost? web = null;
        try
        {
            web = await WebHost.StartAsync(
                new WebHostDependencies(
                    options,
                    overlays,
                    state,
                    sampler,
                    viewers,
                    obs,
                    (evt, _) =>
                    {
                        Write(events, state, evt);
                        return Task.CompletedTask;
                    },
                    log),
                ct).ConfigureAwait(false);

            var composition = new AppComposition(
                options,
                configStore,
                log,
                state,
                sampler,
                database,
                viewers,
                overlays,
                web,
                source,
                obsClient,
                obs,
                automation,
                live,
                points,
                events);

            composition.Subscribe();
            composition.StartLoops();
            return composition;
        }
        catch
        {
            if (web is not null)
                await web.DisposeAsync().ConfigureAwait(false);
            await source.DisposeAsync().ConfigureAwait(false);
            await automation.DisposeAsync().ConfigureAwait(false);
            await obsClient.DisposeAsync().ConfigureAwait(false);
            await overlays.DisposeAsync().ConfigureAwait(false);
            await viewers.DisposeAsync().ConfigureAwait(false);
            await database.DisposeAsync().ConfigureAwait(false);
            await sampler.DisposeAsync().ConfigureAwait(false);
            await log.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_stopped, this);

        if (IsRunning)
            return;

        var host = TikTokEventSource.NormalizeHostId(Options.TikTok.HostId);
        await _source.StartAsync(host, ct).ConfigureAwait(false);
        IsRunning = true;

        Log.Info(
            "app",
            _source is SimulatedEventSource
                ? "Modo simulador activo."
                : $"Conectando con @{host}…");
    }

    public async Task StopSourceAsync(CancellationToken ct = default)
    {
        if (!IsRunning)
            return;

        await _source.StopAsync(ct).ConfigureAwait(false);
        IsRunning = false;
    }

    /// <summary>Inyecta un evento como si viniera del directo (API de prueba y botón de diagnóstico).</summary>
    public void Inject(StreamEvent evt) => Write(_events, State, evt);

    public async Task SaveAsync(CancellationToken ct = default)
    {
        var errors = Options.Validate().ToArray();
        if (errors.Length > 0)
            throw new InvalidDataException(string.Join(" ", errors));

        await _configStore.SaveAsync(Options, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Valida, guarda y aplica reglas de OBS en caliente. La UI edita JSON; el motor recibe
    /// objetos ya validados y reinicia sus cooldowns.
    /// </summary>
    public async Task ApplyRulesAsync(IReadOnlyList<AutomationRule> rules, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rules);

        var previous = Options.Rules;
        Options.Rules = rules.ToList();

        var errors = Options.Validate().ToArray();
        if (errors.Length > 0)
        {
            Options.Rules = previous;
            throw new InvalidDataException(string.Join(" ", errors));
        }

        Automation.ReplaceRules(Options.Rules);
        await _configStore.SaveAsync(Options, ct).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (_stopped)
                return;

            _stopped = true;
        }

        await _stopCts.CancelAsync().ConfigureAwait(false);

        try
        {
            await _source.StopAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn("app", "El origen de eventos no se detuvo limpiamente.", ex);
        }

        _events.Writer.TryComplete();
        await AwaitQuietlyAsync(_pipeline).ConfigureAwait(false);

        foreach (var loop in _loops)
            await AwaitQuietlyAsync(loop).ConfigureAwait(false);

        try
        {
            // Las ráfagas que quedaron abiertas se abonan antes de cerrar la base.
            await Live.FlushGiftsAsync(TimeSpan.Zero, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn("app", "No se pudieron cerrar las ráfagas pendientes.", ex);
        }

        await Automation.DisposeAsync().ConfigureAwait(false);
        await _source.DisposeAsync().ConfigureAwait(false);
        await Viewers.FlushAsync(CancellationToken.None).ConfigureAwait(false);

        try
        {
            await _configStore.SaveAsync(Options, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn("config", "No se pudo guardar la configuración al cerrar.", ex);
        }

        await Web.DisposeAsync().ConfigureAwait(false);
        await ObsClient.DisposeAsync().ConfigureAwait(false);
        await Overlays.DisposeAsync().ConfigureAwait(false);
        await Viewers.DisposeAsync().ConfigureAwait(false);
        await Database.DisposeAsync().ConfigureAwait(false);
        await Resources.DisposeAsync().ConfigureAwait(false);
        await Log.DisposeAsync().ConfigureAwait(false);
        _stopCts.Dispose();
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private void Subscribe()
    {
        _source.Received += evt =>
        {
            if (evt is ViewerCountEvent viewers)
                State.SetViewers(viewers.Count);

            Write(_events, State, evt);
        };

        _source.StateChanged += (sourceState, detail) =>
        {
            State.SetTikTok(Describe(sourceState), Options.TikTok.HostId);
            Log.Info("tiktok", $"Estado {sourceState}{(string.IsNullOrWhiteSpace(detail) ? string.Empty : $": {detail}")}");
        };
    }

    private void StartLoops()
    {
        var pipeline = new EventPipeline(new IStreamEventHandler[] { Live, Automation }, State, Log);
        _pipeline = Task.Run(() => pipeline.RunAsync(_events.Reader, _stopCts.Token), CancellationToken.None);

        _loops.Add(Task.Run(() => Overlays.RunPumpAsync(_stopCts.Token), CancellationToken.None));
        _loops.Add(Task.Run(() => AlertTickAsync(_stopCts.Token), CancellationToken.None));
        _loops.Add(Task.Run(() => StatsTickAsync(_stopCts.Token), CancellationToken.None));
        _loops.Add(Task.Run(() => WatchTickAsync(_stopCts.Token), CancellationToken.None));
        _loops.Add(Task.Run(() => ObsWatchAsync(_stopCts.Token), CancellationToken.None));
    }

    private async Task AlertTickAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    Live.FlushAlerts();
                }
                catch (Exception ex)
                {
                    Log.Warn("alerts", "No se pudieron publicar las alertas agrupadas.", ex);
                }

                await Task.Delay(AlertTickMs, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Apagado normal.
        }
    }

    private async Task StatsTickAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(StatsTickSeconds), ct).ConfigureAwait(false);

                if (Overlays.ClientCount == 0)
                    continue;

                try
                {
                    var snapshot = State.Snapshot();
                    var top = await Viewers.GetTopAsync(3, ct).ConfigureAwait(false);
                    var payload = new
                    {
                        viewers = snapshot.Viewers,
                        tracked = Viewers.TrackedCount,
                        events = snapshot.EventsProcessed,
                        uptimeSec = (long)snapshot.Uptime.TotalSeconds,
                        top = top.Select(v => new { user = v.UniqueId, nickname = v.Nickname, points = v.Points }),
                    };

                    await Overlays
                        .BroadcastAsync(OverlayChannel.Stats, OverlayMessages.Stats(payload), ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Log.Warn("overlay", "No se pudo publicar el panel de estadísticas.", ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Apagado normal.
        }
    }

    private async Task WatchTickAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(WatchTickSeconds), ct).ConfigureAwait(false);

                try
                {
                    await Points.AwardWatchTimeAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Log.Warn("points", "No se pudo abonar la permanencia.", ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Apagado normal.
        }
    }

    private async Task ObsWatchAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(5);
        var reportedAuthError = false;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (ObsClient.IsConnected)
                {
                    var snapshot = await Obs.GetSnapshotAsync(ct).ConfigureAwait(false);
                    State.SetObs(snapshot.Connected, snapshot.CurrentScene);
                    delay = TimeSpan.FromSeconds(5);
                    reportedAuthError = false;
                }
                else if (Options.Obs.AutoConnect)
                {
                    await ObsClient.ConnectAsync(ct).ConfigureAwait(false);
                    var snapshot = await Obs.GetSnapshotAsync(ct).ConfigureAwait(false);
                    State.SetObs(snapshot.Connected, snapshot.CurrentScene);
                    Log.Info("obs", $"Conectado a OBS ({Options.Obs.Url}).");
                    delay = TimeSpan.FromSeconds(5);
                    reportedAuthError = false;
                }
            }
            catch (ObsAuthenticationException ex)
            {
                State.SetObs(false);
                if (!reportedAuthError)
                {
                    Log.Error("obs", ex.Message);
                    reportedAuthError = true;
                }

                delay = TimeSpan.FromSeconds(30);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                State.SetObs(false);
                Log.Debug("obs", $"OBS no disponible: {ex.Message}");
                delay = TimeSpan.FromSeconds(Math.Min(30, delay.TotalSeconds * 2));
            }

            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static IStreamEventSource CreateSource(StreamKitOptions options, IClock clock, IAppLog log)
    {
        var canUseReal = !options.TikTok.Simulator
                         && !string.IsNullOrWhiteSpace(options.TikTok.SigningKey)
                         && !string.IsNullOrWhiteSpace(options.TikTok.HostId);

        return canUseReal
            ? new TikTokEventSource(options.TikTok, clock, log)
            : new SimulatedEventSource(options.TikTok, clock, log);
    }

    private static void Write(Channel<StreamEvent> events, RuntimeState state, StreamEvent evt)
    {
        if (!events.Writer.TryWrite(evt))
            state.MarkEventDropped();
    }

    private static string Describe(SourceState state) => state switch
    {
        SourceState.Connecting => "conectando",
        SourceState.Connected => "conectado",
        SourceState.Reconnecting => "reconectando",
        SourceState.Error => "error",
        _ => "desconectado",
    };

    private static async Task AwaitQuietlyAsync(Task? task)
    {
        if (task is null)
            return;

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Apagado normal.
        }
        catch
        {
            // Un bucle roto no debe impedir el cierre.
        }
    }

    private static string CreateToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private sealed class OverlayOutput(OverlayHub hub) : ILiveOutput
    {
        public void EnqueueAlert(AlertItem item) => hub.Enqueue(item);

        public ValueTask ShowChatAsync(ChatEvent chat, CancellationToken ct)
            => new(hub.BroadcastAsync(OverlayChannel.Chat, OverlayMessages.Chat(chat), ct));

        public ValueTask ShowBotMessageAsync(string text, bool isError, CancellationToken ct)
            => new(hub.BroadcastAsync(OverlayChannel.Chat, OverlayMessages.BotMessage(text, isError), ct));
    }
}
