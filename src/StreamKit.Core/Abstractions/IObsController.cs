namespace StreamKit.Core.Abstractions;

public sealed record ObsSnapshot(
    bool Connected,
    string? CurrentScene,
    IReadOnlyList<string> Scenes,
    DateTimeOffset At);

/// <summary>Contrato de control de OBS. El módulo Obs lo implementa sobre obs-websocket v5.</summary>
public interface IObsController
{
    bool IsConnected { get; }

    Task<ObsSnapshot> GetSnapshotAsync(CancellationToken ct);

    Task<bool> SetSceneAsync(string scene, CancellationToken ct);

    Task<bool> SetFilterEnabledAsync(string source, string filter, bool enabled, CancellationToken ct);

    Task<bool> SetSourceVisibleAsync(string scene, string source, bool visible, CancellationToken ct);

    Task<bool> TriggerHotkeyAsync(string hotkey, CancellationToken ct);

    Task<bool> SetMuteAsync(string source, bool muted, CancellationToken ct);

    Task<bool> SendRawAsync(string requestType, IReadOnlyDictionary<string, object?>? data, CancellationToken ct);
}
