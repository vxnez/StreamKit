using StreamKit.Core.Abstractions;
using StreamKit.Core.Events;
using StreamKit.Core.Logging;
using StreamKit.Core.Options;
using TikTokLiveSharp.Client;
using TikTokLiveSharp.Client.Config;
using TikTokLiveSharp.Errors.Connections;
using TikTokLiveSharp.Events;
using TikTokLiveSharp.Events.Objects;

namespace StreamKit.TikTok;

/// <summary>
/// Conexión real con TikTok LIVE. Un único cliente y un supervisor que reconecta con backoff
/// exponencial. <c>RunAsync</c> vuelve en cuanto el socket queda abierto, así que el supervisor
/// espera la caída en vez de asumir que la llamada bloquea hasta el final del directo.
/// </summary>
public sealed class TikTokEventSource : IStreamEventSource
{
    private const string Category = "tiktok";
    private const int MinBackoffSeconds = 2;
    private const int MaxBackoffSeconds = 300;
    private const int DisconnectPollSeconds = 2;
    private const int StopTimeoutSeconds = 5;

    private readonly TikTokOptions _options;
    private readonly IClock _clock;
    private readonly IAppLog _log;
    private readonly object _sync = new();

    private CancellationTokenSource? _cts;
    private Task? _supervisor;
    private TikTokLiveClient? _client;
    private int _state = (int)SourceState.Disconnected;
    private bool _disposed;

    public TikTokEventSource(TikTokOptions options, IClock clock, IAppLog? log = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? NullAppLog.Instance;
    }

    public string Name => "tiktok";

    public SourceState State => (SourceState)Volatile.Read(ref _state);

    public event Action<SourceState, string?>? StateChanged;

    public event Action<StreamEvent>? Received;

    /// <summary>
    /// Acepta el usuario suelto o una URL del canal: <c>@ana</c>, <c>ana</c> y
    /// <c>https://www.tiktok.com/@ana/live</c> producen el mismo identificador.
    /// </summary>
    public static string NormalizeHostId(string? hostId)
    {
        var value = (hostId ?? string.Empty).Trim();
        if (value.Length == 0)
            return string.Empty;

        var at = value.LastIndexOf('@');
        if (at >= 0)
            value = value[(at + 1)..];

        var cut = value.IndexOfAny(['/', '?', '#', ' ']);
        if (cut >= 0)
            value = value[..cut];

        return value.Trim();
    }

    public Task StartAsync(string hostId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var host = NormalizeHostId(hostId);
        if (host.Length == 0)
            throw new ArgumentException("Indicá el usuario de TikTok, por ejemplo @micanal.", nameof(hostId));

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_supervisor is { IsCompleted: false })
                throw new InvalidOperationException("La conexión con TikTok ya está en marcha.");

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _supervisor = Task.Run(() => SuperviseAsync(host, token), CancellationToken.None);
        }

        SetState(SourceState.Connecting, $"Conectando con @{host}…");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        CancellationTokenSource? cts;
        Task? supervisor;
        TikTokLiveClient? client;

        lock (_sync)
        {
            cts = _cts;
            _cts = null;
            supervisor = _supervisor;
            _supervisor = null;
            client = _client;
            _client = null;
        }

        if (cts is null && supervisor is null)
        {
            SetState(SourceState.Disconnected, null);
            return;
        }

        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Ya estaba cerrado.
        }

        if (client is not null)
            await StopClientAsync(client).ConfigureAwait(false);

        if (supervisor is not null)
        {
            try
            {
                await supervisor.WaitAsync(TimeSpan.FromSeconds(StopTimeoutSeconds), ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _log.Warn(Category, "La conexión con TikTok no terminó a tiempo.");
            }
            catch (OperationCanceledException)
            {
                // El llamador se rindió; el supervisor sigue su curso acotado.
            }
            catch (Exception ex)
            {
                _log.Debug(Category, $"Cierre de la supervisión: {ex.Message}");
            }
        }

        cts?.Dispose();
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

    private async Task SuperviseAsync(string host, CancellationToken ct)
    {
        var attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            var client = CreateClient(host);

            lock (_sync)
            {
                _client = client;
            }

            try
            {
                Exception? connectError = null;
                await client.RunAsync(ct, ex => connectError = ex).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();

                if (!client.Connected)
                {
                    throw new InvalidOperationException(
                        connectError?.Message ?? "TikTok cerró la conexión antes de abrir el socket.",
                        connectError);
                }

                attempt = 0;
                SetState(SourceState.Connected, null);
                _log.Info(Category, $"Conectado al directo de @{host}.");

                var reason = await WaitForDisconnectAsync(client, ct).ConfigureAwait(false);
                if (reason == DisconnectReason.LiveEnded)
                {
                    _log.Info(Category, $"El directo de @{host} terminó.");
                    Emit(new StreamEndedEvent(_clock.UtcNow));
                    SetState(SourceState.Disconnected, "El directo terminó.");
                    return;
                }

                if (reason == DisconnectReason.Cancelled)
                    break;

                _log.Warn(Category, $"Se perdió la conexión con @{host}.");
                SetState(SourceState.Reconnecting, "Se perdió la conexión con TikTok.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (LiveNotFoundException ex)
            {
                _log.Warn(Category, $"@{host} no está en directo. Se reintentará.", ex);
                SetState(SourceState.Reconnecting, "El canal no está en directo.");
            }
            catch (Exception ex)
            {
                _log.Warn(Category, $"No se pudo conectar con @{host}.", ex);
                SetState(SourceState.Reconnecting, ex.Message);
            }
            finally
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_client, client))
                        _client = null;
                }

                await StopClientAsync(client).ConfigureAwait(false);
            }

            if (ct.IsCancellationRequested)
                break;

            attempt++;
            var delay = Backoff(attempt);
            _log.Debug(Category, $"Reintento {attempt} en {delay.TotalSeconds:0} s.");

            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        SetState(SourceState.Disconnected, null);
    }

    /// <summary>
    /// La librería no avisa de un cierre limpio del socket (solo de los errores), así que además
    /// de escuchar los eventos se comprueba el estado del socket cada dos segundos.
    /// </summary>
    private async Task<DisconnectReason> WaitForDisconnectAsync(TikTokLiveClient client, CancellationToken ct)
    {
        var signal = new TaskCompletionSource<DisconnectReason>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnDisconnected(TikTokLiveClient _, bool __) => signal.TrySetResult(DisconnectReason.SocketClosed);
        void OnLiveEnded(TikTokLiveClient _, ControlMessage __) => signal.TrySetResult(DisconnectReason.LiveEnded);

        client.OnDisconnected += OnDisconnected;
        client.OnLiveEnded += OnLiveEnded;

        try
        {
            while (true)
            {
                try
                {
                    return await signal.Task
                        .WaitAsync(TimeSpan.FromSeconds(DisconnectPollSeconds), ct)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    if (client.Connected)
                        continue;

                    // La librería reconecta el socket por su cuenta; solo se da por perdido si
                    // sigue caído tras la ventana de gracia.
                    await Task.Delay(TimeSpan.FromSeconds(DisconnectPollSeconds), ct).ConfigureAwait(false);
                    if (!client.Connected)
                        return DisconnectReason.SocketClosed;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return DisconnectReason.Cancelled;
        }
        finally
        {
            client.OnDisconnected -= OnDisconnected;
            client.OnLiveEnded -= OnLiveEnded;
        }
    }

    private async Task StopClientAsync(TikTokLiveClient client)
    {
        try
        {
            var stop = client.Stop();
            if (await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(StopTimeoutSeconds))).ConfigureAwait(false) == stop)
                await stop.ConfigureAwait(false);
            else
                _log.Warn(Category, "El cliente de TikTok no cerró a tiempo.");
        }
        catch (Exception ex)
        {
            _log.Debug(Category, $"Cierre del cliente de TikTok: {ex.Message}");
        }
    }

    private TimeSpan Backoff(int attempt)
    {
        var max = Math.Clamp(_options.ReconnectMaxSeconds, MinBackoffSeconds, MaxBackoffSeconds);
        var seconds = Math.Min(max, Math.Pow(2, Math.Min(attempt, 5)));
        return TimeSpan.FromSeconds(seconds);
    }

    private TikTokLiveClient CreateClient(string host)
    {
        // retryConnection queda en false a propósito: el reintento interno de la librería es un
        // bucle de un segundo que martilla el servidor de firma. El backoff lo lleva el supervisor.
        var client = new TikTokLiveClient(
            uniqueID: host,
            timeout: 20f,
            reconnectInterval: 2f,
            pollingInterval: 1f,
            roomId: string.Empty,
            enableCompression: true,
            skipRoomInfo: false,
            clientParams: null,
            processInitialData: false,
            enableExtendedGiftInfo: false,
            proxyHandler: null,
            lang: "en-US",
            socketBufferSize: 500_000,
            logDebug: false,
            logLevel: LogLevel.None,
            printMessageData: false,
            checkForUnparsedData: false,
            customSigningServer: string.IsNullOrWhiteSpace(_options.SignServerUrl) ? null : _options.SignServerUrl.Trim(),
            signingServerApiKey: string.IsNullOrWhiteSpace(_options.SigningKey) ? null : _options.SigningKey.Trim());

        client.OnConnected += (_, _) => SetState(SourceState.Connected, null);
        client.OnException += (_, ex) => _log.Warn(Category, "El socket de TikTok informó un error.", ex);

        client.OnChatMessage += (_, chat) =>
            Emit(TikTokEventMapper.MapChat(MapChat(chat), _clock.UtcNow));

        client.OnGift += OnGiftReceived;

        client.OnLike += (_, like) =>
            Emit(TikTokEventMapper.MapLike(new TikTokLikeMessage(MapUser(like.Sender), like.Count), _clock.UtcNow));

        client.OnFollow += (_, follow) =>
            Emit(TikTokEventMapper.MapFollow(new TikTokFollowMessage(MapUser(follow.User)), _clock.UtcNow));

        client.OnShare += (_, share) =>
            Emit(TikTokEventMapper.MapShare(new TikTokShareMessage(MapUser(share.User)), _clock.UtcNow));

        client.OnJoin += (_, join) =>
            Emit(TikTokEventMapper.MapJoin(new TikTokJoinMessage(MapUser(join.User), join.ViewerCount), _clock.UtcNow));

        client.OnSubNotify += (_, sub) =>
            Emit(TikTokEventMapper.MapSubscribe(
                new TikTokSubscribeMessage(MapUser(sub.Sender), (int)Math.Clamp(sub.SubMonth, 0, int.MaxValue)),
                _clock.UtcNow));

        client.OnRoomUpdate += (_, room) =>
            Emit(TikTokEventMapper.MapViewers(new TikTokViewerCountMessage(room.NumberOfViewers), _clock.UtcNow));

        return client;
    }

    private void OnGiftReceived(TikTokLiveClient _, TikTokGift gift)
    {
        if (gift is null)
            return;

        if (gift.StreakFinished)
        {
            EmitGift(gift);
            return;
        }

        gift.OnStreakFinished += OnGiftStreakFinished;
    }

    private void OnGiftStreakFinished(TikTokGift gift, long amount) => EmitGift(gift);

    private void EmitGift(TikTokGift gift)
    {
        var definition = gift.Gift;
        var message = new TikTokGiftMessage(
            MapUser(gift.Sender),
            definition?.Id ?? 0,
            definition?.Name ?? string.Empty,
            definition?.DiamondCost ?? 0,
            gift.Amount,
            StreakFinished: true);

        Emit(TikTokEventMapper.MapGift(message, _clock.UtcNow));
    }

    private static TikTokUser MapUser(User? user)
        => user is null
            ? TikTokUser.Empty
            : new TikTokUser(user.Id, user.UniqueId ?? string.Empty, user.NickName ?? string.Empty);

    private static TikTokChatMessage MapChat(Chat chat)
        => new(MapUser(chat.Sender), chat.Message ?? string.Empty);

    private void Emit(StreamEvent? evt)
    {
        if (evt is null)
            return;

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

    private enum DisconnectReason
    {
        Cancelled,
        SocketClosed,
        LiveEnded,
    }
}
