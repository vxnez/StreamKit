using StreamKit.Core.Abstractions;
using StreamKit.Core.Events;
using StreamKit.Core.Logging;
using StreamKit.Core.Options;

namespace StreamKit.TikTok;

/// <summary>
/// Origen sintético para trabajar sin clave de firma: reproduce el mismo flujo de eventos que un
/// directo real a un ritmo controlado. <see cref="EmitNext"/> también alimenta el botón de prueba
/// de la interfaz.
/// </summary>
public sealed class SimulatedEventSource : IStreamEventSource
{
    private const string Category = "simulator";
    private const int MinIntervalMs = 50;
    private const int MaxEventsPerMinute = 6_000;

    private static readonly (string UniqueId, string Nickname)[] People =
    [
        ("ana_plays", "Ana"),
        ("bruno_mc", "Bruno"),
        ("carla.craft", "Carla"),
        ("dani_redstone", "Dani"),
        ("elena_builds", "Elena"),
        ("fede_gg", "Fede"),
        ("gus_minero", "Gus"),
        ("lucia_enderman", "Lucía"),
    ];

    private static readonly string[] ChatLines =
    [
        "!puntos",
        "hola stream",
        "gg",
        "!top",
        "¿jugamos juntos?",
        "!tienda",
        "muy bueno",
        "!ayuda",
    ];

    private static readonly (long Id, string Name, int Diamonds)[] Gifts =
    [
        (5655, "Rosa", 1),
        (5827, "TikTok", 1),
        (6614, "Helado", 5),
        (6782, "Café", 10),
        (8414, "Corona", 99),
    ];

    private readonly TikTokOptions _options;
    private readonly IClock _clock;
    private readonly IAppLog _log;
    private readonly Random _random;
    private readonly object _sync = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _state = (int)SourceState.Disconnected;
    private int _step;
    private int _viewers = 12;
    private bool _disposed;

    public SimulatedEventSource(TikTokOptions options, IClock clock, IAppLog? log = null, int? seed = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? NullAppLog.Instance;
        _random = seed.HasValue ? new Random(seed.Value) : new Random();
    }

    public string Name => "simulator";

    public SourceState State => (SourceState)Volatile.Read(ref _state);

    public event Action<SourceState, string?>? StateChanged;

    public event Action<StreamEvent>? Received;

    /// <summary>Emite el siguiente evento del ciclo fijo chat → regalo → like → follow → share →
    /// join → subscribe → espectadores. Devuelve el evento ya normalizado.</summary>
    public StreamEvent? EmitNext()
    {
        var at = _clock.UtcNow;
        StreamEvent? evt;

        lock (_sync)
        {
            var index = _random.Next(People.Length);
            var person = People[index];
            var user = new TikTokUser(1_000 + index, person.UniqueId, person.Nickname);
            var step = _step++;

            evt = (step % 8) switch
            {
                0 => TikTokEventMapper.MapChat(new TikTokChatMessage(user, ChatLines[_random.Next(ChatLines.Length)]), at),
                1 => MapGift(user, at),
                2 => TikTokEventMapper.MapLike(new TikTokLikeMessage(user, _random.Next(1, 15)), at),
                3 => TikTokEventMapper.MapFollow(new TikTokFollowMessage(user), at),
                4 => TikTokEventMapper.MapShare(new TikTokShareMessage(user), at),
                5 => TikTokEventMapper.MapJoin(new TikTokJoinMessage(user, ++_viewers), at),
                6 => TikTokEventMapper.MapSubscribe(new TikTokSubscribeMessage(user, _random.Next(1, 5)), at),
                _ => MapViewers(at),
            };
        }

        if (evt is not null)
            Raise(evt);

        return evt;
    }

    public Task StartAsync(string hostId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_loop is { IsCompleted: false })
                throw new InvalidOperationException("El simulador ya está en marcha.");

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _loop = Task.Run(() => PumpAsync(token), CancellationToken.None);
        }

        SetState(SourceState.Connecting, "Simulador");
        SetState(SourceState.Connected, null);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        CancellationTokenSource? cts;
        Task? loop;

        lock (_sync)
        {
            cts = _cts;
            _cts = null;
            loop = _loop;
            _loop = null;
        }

        if (cts is null)
        {
            SetState(SourceState.Disconnected, null);
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Ya estaba cerrado.
        }

        if (loop is not null)
        {
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _log.Warn(Category, "El simulador no se detuvo a tiempo.");
            }
            catch (OperationCanceledException)
            {
                // El llamador se rindió.
            }
        }

        cts.Dispose();
        SetState(SourceState.Disconnected, null);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        var perMinute = Math.Clamp(_options.SimulatorEventsPerMinute, 1, MaxEventsPerMinute);
        var interval = TimeSpan.FromMilliseconds(Math.Max(MinIntervalMs, 60_000d / perMinute));

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
                EmitNext();
            }
        }
        catch (OperationCanceledException)
        {
            // Apagado normal.
        }
        finally
        {
            SetState(SourceState.Disconnected, null);
        }
    }

    private GiftEvent? MapGift(TikTokUser user, DateTimeOffset at)
    {
        var gift = Gifts[_random.Next(Gifts.Length)];
        var count = gift.Diamonds >= 99 ? 1 : _random.Next(1, 6);
        return TikTokEventMapper.MapGift(
            new TikTokGiftMessage(user, gift.Id, gift.Name, gift.Diamonds, count, StreakFinished: true),
            at);
    }

    private ViewerCountEvent MapViewers(DateTimeOffset at)
    {
        _viewers = Math.Max(0, _viewers + _random.Next(-1, 3));
        return TikTokEventMapper.MapViewers(new TikTokViewerCountMessage(_viewers), at);
    }

    private void Raise(StreamEvent evt)
    {
        try
        {
            Received?.Invoke(evt);
        }
        catch (Exception ex)
        {
            _log.Error(Category, $"Un suscriptor falló al procesar {evt.Kind}.", ex);
        }
    }

    private void SetState(SourceState state, string? detail)
    {
        var previous = (SourceState)Interlocked.Exchange(ref _state, (int)state);
        if (previous == state)
            return;

        try
        {
            StateChanged?.Invoke(state, detail);
        }
        catch (Exception ex)
        {
            _log.Error(Category, "Un suscriptor falló al procesar el cambio de estado.", ex);
        }
    }
}
