using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using StreamKit.Core.Logging;

namespace StreamKit.Obs;

/// <summary>Resultado de una petición del protocolo v5.</summary>
public sealed record ObsRequestResult(bool Success, int Code, string? Comment, JsonElement? Data);

/// <summary>
/// Cliente mínimo de obs-websocket v5 sobre <see cref="ClientWebSocket"/> y System.Text.Json:
/// Hello/Identify, peticiones correlacionadas por <c>requestId</c>, timeout y reconexión bajo
/// demanda. Sin dependencias externas ni eventos de alta frecuencia.
/// </summary>
public sealed class ObsWebSocketClient : IAsyncDisposable
{
    private const int MaxMessageBytes = 4 * 1024 * 1024;
    private const int RpcVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Uri _uri;
    private readonly string _password;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeSpan _connectTimeout;
    private readonly IAppLog _log;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly Dictionary<string, TaskCompletionSource<ObsRequestResult>> _pending = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    private volatile ClientWebSocket? _socket;
    private Task? _receiveLoop;
    private CancellationTokenSource? _loopCts;
    private bool _disposed;

    public ObsWebSocketClient(
        string url,
        string password,
        TimeSpan requestTimeout,
        IAppLog? log = null,
        TimeSpan? connectTimeout = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "ws")
            throw new ArgumentException("La URL de OBS debe ser ws://host:puerto.", nameof(url));

        _uri = uri;
        _password = password ?? string.Empty;
        _requestTimeout = requestTimeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(5) : requestTimeout;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(5);
        _log = log ?? NullAppLog.Instance;
    }

    public bool IsConnected => _socket is { State: WebSocketState.Open };

    /// <summary>Eventos de OBS (op 5). Solo llegan si se pidió una suscripción distinta de 0.</summary>
    public event Action<string, JsonElement>? EventReceived;

    public async Task ConnectAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsConnected)
                return;

            await CloseSocketAsync().ConfigureAwait(false);

            var socket = new ClientWebSocket();
            socket.Options.AddSubProtocol("obswebsocket.json");
            _socket = socket;

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(_connectTimeout);

                await socket.ConnectAsync(_uri, timeout.Token).ConfigureAwait(false);
                await HandshakeAsync(socket, timeout.Token).ConfigureAwait(false);
            }
            catch (ObsConnectionClosedException ex) when (ex.CloseStatus == (WebSocketCloseStatus)4009)
            {
                socket.Dispose();
                _socket = null;
                throw new ObsAuthenticationException("La contraseña de obs-websocket no es válida.", ex);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                socket.Dispose();
                _socket = null;
                throw new ObsConnectionException(
                    $"OBS no respondió en {_connectTimeout.TotalSeconds:0.#} s en {_uri}.");
            }
            catch (Exception ex) when (ex is not ObsConnectionException and not ObsAuthenticationException)
            {
                socket.Dispose();
                _socket = null;
                throw new ObsConnectionException($"No se pudo conectar con OBS en {_uri}.", ex);
            }

            _loopCts = new CancellationTokenSource();
            _receiveLoop = Task.Run(() => ReceiveLoopAsync(socket, _loopCts.Token), CancellationToken.None);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    /// <summary>Abre la conexión si hace falta y envía una petición, esperando su respuesta.</summary>
    public async Task<ObsRequestResult> SendRequestAsync(
        string requestType,
        IReadOnlyDictionary<string, object?>? data,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestType);

        if (!IsConnected)
            await ConnectAsync(ct).ConfigureAwait(false);

        var socket = _socket;
        if (socket is null)
            throw new ObsConnectionException("La conexión con OBS no está abierta.");

        var requestId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<ObsRequestResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_sync)
        {
            _pending[requestId] = completion;
        }

        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["op"] = 6,
                ["d"] = new Dictionary<string, object?>
                {
                    ["requestType"] = requestType,
                    ["requestId"] = requestId,
                    ["requestData"] = data,
                },
            };

            await SendJsonAsync(socket, payload, ct).ConfigureAwait(false);
            return await completion.Task.WaitAsync(_requestTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new ObsRequestTimeoutException(requestType, _requestTimeout);
        }
        finally
        {
            lock (_sync)
            {
                _pending.Remove(requestId);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        await CloseSocketAsync().ConfigureAwait(false);
        _connectGate.Dispose();
    }

    private async Task HandshakeAsync(ClientWebSocket socket, CancellationToken ct)
    {
        using var hello = await ReceiveJsonAsync(socket, ct).ConfigureAwait(false);
        var helloRoot = hello.RootElement;

        if (GetInt32(helloRoot, "op") != 0 || !helloRoot.TryGetProperty("d", out var helloData))
            throw new ObsProtocolException("OBS no envió el mensaje Hello esperado.");

        var rpcVersion = GetInt32(helloData, "rpcVersion");
        if (rpcVersion < 1)
            throw new ObsProtocolException($"OBS informó una versión de RPC no soportada ({rpcVersion}).");

        var identify = new Dictionary<string, object?>
        {
            ["rpcVersion"] = RpcVersion,
            ["eventSubscriptions"] = 0,
        };

        if (helloData.TryGetProperty("authentication", out var authentication))
        {
            if (string.IsNullOrEmpty(_password))
                throw new ObsAuthenticationException(
                    "OBS exige contraseña. Configurala en Ajustes → OBS de StreamKit.");

            var salt = GetString(authentication, "salt");
            var challenge = GetString(authentication, "challenge");
            if (string.IsNullOrEmpty(salt) || string.IsNullOrEmpty(challenge))
                throw new ObsProtocolException("OBS envió un desafío de autenticación incompleto.");

            identify["authentication"] = ObsAuth.CreateAuthenticationString(_password, salt, challenge);
        }

        await SendJsonAsync(socket, new Dictionary<string, object?> { ["op"] = 1, ["d"] = identify }, ct)
            .ConfigureAwait(false);

        using var identified = await ReceiveJsonAsync(socket, ct).ConfigureAwait(false);
        if (GetInt32(identified.RootElement, "op") != 2)
            throw new ObsProtocolException("OBS no confirmó la identificación.");
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                using var document = await ReceiveJsonAsync(socket, ct).ConfigureAwait(false);
                Dispatch(document.RootElement);
            }
        }
        catch (OperationCanceledException)
        {
            // Cierre normal.
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested)
                return;

            _log.Warn("obs", "Se perdió la conexión con OBS.", ex);
            FailPending(new ObsConnectionClosedException("Se perdió la conexión con OBS.", socket.CloseStatus, ex));
        }
    }

    private void Dispatch(JsonElement root)
    {
        switch (GetInt32(root, "op"))
        {
            case 5:
                if (!root.TryGetProperty("d", out var eventData))
                    return;

                var type = GetString(eventData, "eventType");
                if (string.IsNullOrEmpty(type))
                    return;

                var payload = eventData.TryGetProperty("eventData", out var body) ? body.Clone() : default;
                try
                {
                    EventReceived?.Invoke(type, payload);
                }
                catch (Exception ex)
                {
                    _log.Error("obs", $"Un suscriptor falló al procesar el evento {type}.", ex);
                }

                break;

            case 7:
                if (root.TryGetProperty("d", out var response))
                    HandleResponse(response);
                break;
        }
    }

    private void HandleResponse(JsonElement data)
    {
        var requestId = GetString(data, "requestId");
        if (string.IsNullOrEmpty(requestId))
            return;

        TaskCompletionSource<ObsRequestResult>? completion;
        lock (_sync)
        {
            if (!_pending.TryGetValue(requestId, out completion))
                return;
        }

        var status = data.TryGetProperty("requestStatus", out var value) ? value : default;
        var result = new ObsRequestResult(
            status.ValueKind == JsonValueKind.Object && status.TryGetProperty("result", out var ok) && ok.GetBoolean(),
            status.ValueKind == JsonValueKind.Object ? GetInt32(status, "code") : 0,
            status.ValueKind == JsonValueKind.Object ? GetString(status, "comment") : null,
            data.TryGetProperty("responseData", out var responseData) ? responseData.Clone() : null);

        completion.TrySetResult(result);
    }

    private static async Task<JsonDocument> ReceiveJsonAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(8 * 1024);
        try
        {
            using var stream = new MemoryStream();

            while (true)
            {
                var result = await socket.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new ObsConnectionClosedException(
                        $"OBS cerró la conexión ({socket.CloseStatus?.ToString() ?? "sin código"}).",
                        socket.CloseStatus);
                }

                stream.Write(buffer, 0, result.Count);

                if (stream.Length > MaxMessageBytes)
                    throw new ObsProtocolException("OBS envió un mensaje demasiado grande.");

                if (result.EndOfMessage)
                    return JsonDocument.Parse(stream.ToArray());
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static Task SendJsonAsync(ClientWebSocket socket, object payload, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        return socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    private async Task CloseSocketAsync()
    {
        var socket = _socket;
        var loop = _receiveLoop;
        var loopCts = _loopCts;

        _socket = null;
        _receiveLoop = null;
        _loopCts = null;

        if (loopCts is not null)
        {
            try
            {
                await loopCts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // Ya estaba cerrado.
            }

            loopCts.Dispose();
        }

        if (socket is not null)
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "StreamKit", timeout.Token)
                        .ConfigureAwait(false);
                }
            }
            catch
            {
                // El cierre es best-effort: OBS puede haberse ido antes.
            }
            finally
            {
                socket.Dispose();
            }
        }

        if (loop is not null)
        {
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
                // El bucle ya terminó o quedó abortado.
            }
        }

        FailPending(new ObsConnectionClosedException("La conexión con OBS se cerró."));
    }

    private void FailPending(Exception error)
    {
        TaskCompletionSource<ObsRequestResult>[] pending;
        lock (_sync)
        {
            pending = _pending.Values.ToArray();
            _pending.Clear();
        }

        foreach (var completion in pending)
            completion.TrySetException(error);
    }

    private static int GetInt32(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;

    private static string? GetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
