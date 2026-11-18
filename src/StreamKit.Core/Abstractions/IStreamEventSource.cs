using StreamKit.Core.Events;

namespace StreamKit.Core.Abstractions;

public enum SourceState
{
    Disconnected = 0,
    Connecting = 1,
    Connected = 2,
    Reconnecting = 3,
    Error = 4,
}

/// <summary>Origen de eventos del directo (TikTok real o simulador).</summary>
public interface IStreamEventSource : IAsyncDisposable
{
    string Name { get; }

    SourceState State { get; }

    /// <summary>Se invoca al cambiar de estado; puede venir de cualquier hilo.</summary>
    event Action<SourceState, string?>? StateChanged;

    /// <summary>Eventos normalizados. Se invoca desde el hilo de red: no bloquear.</summary>
    event Action<StreamEvent>? Received;

    Task StartAsync(string hostId, CancellationToken ct);

    Task StopAsync(CancellationToken ct);
}
