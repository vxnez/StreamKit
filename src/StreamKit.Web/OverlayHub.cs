using System.Collections.Concurrent;
using StreamKit.Core.Alerts;
using StreamKit.Core.Logging;
using StreamKit.Core.Options;
using StreamKit.Core.State;

namespace StreamKit.Web;

public enum OverlayChannel
{
    Alerts = 0,
    Chat = 1,
    Stats = 2,
}

/// <summary>Abstracción mínima del socket para poder testear el hub sin red.</summary>
public interface IOverlayConnection
{
    Guid Id { get; }

    OverlayChannel Channel { get; }

    ValueTask SendAsync(string json, CancellationToken ct);

    ValueTask CloseAsync(string reason, CancellationToken ct);
}

/// <summary>
/// Reparte mensajes a los Browser Sources y garantiza que las alertas se muestren de a una:
/// no entrega la siguiente hasta recibir ack (o vencer el margen).
/// </summary>
public sealed class OverlayHub : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, IOverlayConnection> _clients = new();
    private readonly AlertQueue _queue;
    private readonly StreamKitOptions _options;
    private readonly IAppLog _log;
    private readonly RuntimeState _state;
    private readonly SemaphoreSlim _wake = new(0);

    private AlertItem? _active;
    private TaskCompletionSource<bool>? _ack;
    private int _disposed;

    public OverlayHub(AlertQueue queue, StreamKitOptions options, IAppLog log, RuntimeState state)
    {
        _queue = queue;
        _options = options;
        _log = log;
        _state = state;
    }

    public int ClientCount => _clients.Count;

    public int QueueDepth => _queue.Count;

    public string? ActiveAlertId => _active?.Id;

    public bool TryRegister(IOverlayConnection connection)
    {
        if (_clients.Count >= Math.Max(1, _options.Alerts.MaxClients))
        {
            _log.Warn("overlay", "Se rechazó un overlay: límite de clientes alcanzado.");
            return false;
        }

        _clients[connection.Id] = connection;
        _log.Info("overlay", $"Overlay conectado ({connection.Channel}). Total: {_clients.Count}.");
        ReleaseWake();
        return true;
    }

    public async Task UnregisterAsync(Guid id)
    {
        if (!_clients.TryRemove(id, out var connection))
            return;

        _log.Info("overlay", $"Overlay desconectado ({connection.Channel}). Total: {_clients.Count}.");

        // Si se cayó con una alerta en pantalla, el bombeo la vuelve a encolar.
        if (connection.Channel == OverlayChannel.Alerts)
            Volatile.Read(ref _ack)?.TrySetResult(false);

        await Task.CompletedTask;
    }

    public void Enqueue(AlertItem item)
    {
        if (item.Sequence == 0)
            item.Sequence = _queue.NextSequence();

        if (!_queue.TryEnqueue(item))
        {
            _state.MarkAlertDropped();
            _log.Debug("overlay", $"Alerta descartada (cola llena): {item.Kind}.");
            return;
        }

        _state.SetQueueDepth(_queue.Count);
        ReleaseWake();
    }

    public Task BroadcastAsync(OverlayChannel channel, string json, CancellationToken ct)
        => SendToChannelAsync(channel, json, ct);

    public void Ack(string alertId)
    {
        if (_active is null || !string.Equals(_active.Id, alertId, StringComparison.Ordinal))
            return;

        _ack?.TrySetResult(true);
    }

    public async Task RunPumpAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && Volatile.Read(ref _disposed) == 0)
        {
            try
            {
                var item = _queue.Dequeue();

                if (item is null)
                {
                    await _wake.WaitAsync(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
                    continue;
                }

                if (!await WaitForAlertClientAsync(ct).ConfigureAwait(false))
                {
                    _state.MarkAlertDropped();
                    _log.Warn("overlay", $"Sin overlay conectado: se descartó una alerta de {item.Kind}.");
                    continue;
                }

                // El ack se publica antes de enviar: si el overlay se cae durante el envío,
                // Unregister ya puede señalizarlo y la alerta vuelve a la cola.
                var ack = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _active = item;
                Volatile.Write(ref _ack, ack);
                _state.SetQueueDepth(_queue.Count);

                await SendToChannelAsync(OverlayChannel.Alerts, OverlayMessages.Alert(item), ct)
                    .ConfigureAwait(false);

                var timeoutMs = Math.Max(500, item.DurationMs + _options.Alerts.AckGraceMs);
                var timeout = Task.Delay(timeoutMs, ct);
                var completed = await Task.WhenAny(ack.Task, timeout).ConfigureAwait(false);

                _active = null;
                Volatile.Write(ref _ack, null);

                if (completed == timeout)
                {
                    _log.Warn("overlay", $"Alerta {item.Id} sin ack en {timeoutMs} ms; se continúa.");
                }
                else if (!await ack.Task.ConfigureAwait(false))
                {
                    // El overlay se cayó con la alerta en pantalla: vuelve a la cola.
                    if (_queue.TryEnqueue(item))
                        _state.SetQueueDepth(_queue.Count);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.Error("overlay", "Error en el bombeo de alertas.", ex);
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _ack?.TrySetResult(false);

        foreach (var client in _clients.Values)
        {
            try
            {
                await client.CloseAsync("servidor apagándose", CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // el socket ya estaba cerrado
            }
        }

        _clients.Clear();
        _wake.Dispose();
    }

    private void ReleaseWake()
    {
        if (_wake.CurrentCount == 0)
            _wake.Release();
    }

    private async Task<bool> WaitForAlertClientAsync(CancellationToken ct)
    {
        if (HasAlertClients())
            return true;

        var holdSeconds = Math.Max(1, _options.Alerts.NoClientHoldSeconds);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(holdSeconds);

        while (!HasAlertClients())
        {
            if (DateTimeOffset.UtcNow >= deadline)
                return false;

            await Task.Delay(250, ct).ConfigureAwait(false);
        }

        return true;
    }

    private bool HasAlertClients()
        => _clients.Values.Any(static c => c.Channel == OverlayChannel.Alerts);

    private async Task SendToChannelAsync(OverlayChannel channel, string json, CancellationToken ct)
    {
        foreach (var client in _clients.Values)
        {
            if (client.Channel != channel)
                continue;

            try
            {
                await client.SendAsync(json, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Warn("overlay", $"Fallo al enviar a un overlay: {ex.Message}");
                await UnregisterAsync(client.Id).ConfigureAwait(false);
            }
        }
    }
}
