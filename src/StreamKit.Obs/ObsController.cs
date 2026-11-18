using System.Text.Json;
using StreamKit.Core.Abstractions;
using StreamKit.Core.Logging;

namespace StreamKit.Obs;

/// <summary>
/// Implementación de <see cref="IObsController"/> sobre obs-websocket v5. Traduce el contrato del
/// dominio a peticiones concretas y convierte cualquier fallo en <c>false</c> (con log), para que
/// una regla rota nunca tumbe el pipeline de eventos.
/// </summary>
public sealed class ObsController : IObsController
{
    private const string Category = "obs";

    private readonly ObsWebSocketClient _client;
    private readonly IClock _clock;
    private readonly IAppLog _log;

    public ObsController(ObsWebSocketClient client, IClock clock, IAppLog? log = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? NullAppLog.Instance;
    }

    public bool IsConnected => _client.IsConnected;

    public async Task<ObsSnapshot> GetSnapshotAsync(CancellationToken ct)
    {
        if (!_client.IsConnected)
            return Disconnected();

        try
        {
            var result = await _client.SendRequestAsync("GetSceneList", null, ct).ConfigureAwait(false);
            if (!result.Success || result.Data is not { } data)
                return Disconnected();

            var scenes = new List<string>();
            if (data.TryGetProperty("scenes", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var scene in list.EnumerateArray())
                {
                    var name = GetString(scene, "sceneName");
                    if (!string.IsNullOrEmpty(name))
                        scenes.Add(name);
                }
            }

            return new ObsSnapshot(true, GetString(data, "currentProgramSceneName"), scenes, _clock.UtcNow);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn(Category, "No se pudo leer el estado de OBS.", ex);
            return Disconnected();
        }
    }

    public Task<bool> SetSceneAsync(string scene, CancellationToken ct)
        => RequestAsync("SetCurrentProgramScene", new Dictionary<string, object?> { ["sceneName"] = scene }, ct);

    public Task<bool> SetFilterEnabledAsync(string source, string filter, bool enabled, CancellationToken ct)
        => RequestAsync(
            "SetSourceFilterEnabled",
            new Dictionary<string, object?>
            {
                ["sourceName"] = source,
                ["filterName"] = filter,
                ["filterEnabled"] = enabled,
            },
            ct);

    public async Task<bool> SetSourceVisibleAsync(string scene, string source, bool visible, CancellationToken ct)
    {
        var id = await FindSceneItemIdAsync(scene, source, ct).ConfigureAwait(false);
        if (id is null)
            return false;

        return await RequestAsync(
            "SetSceneItemEnabled",
            new Dictionary<string, object?>
            {
                ["sceneName"] = scene,
                ["sceneItemId"] = id.Value,
                ["sceneItemEnabled"] = visible,
            },
            ct).ConfigureAwait(false);
    }

    public Task<bool> TriggerHotkeyAsync(string hotkey, CancellationToken ct)
        => RequestAsync("TriggerHotkeyByName", new Dictionary<string, object?> { ["hotkeyName"] = hotkey }, ct);

    public Task<bool> SetMuteAsync(string source, bool muted, CancellationToken ct)
        => RequestAsync(
            "SetInputMute",
            new Dictionary<string, object?> { ["inputName"] = source, ["inputMuted"] = muted },
            ct);

    public Task<bool> SendRawAsync(
        string requestType,
        IReadOnlyDictionary<string, object?>? data,
        CancellationToken ct)
        => RequestAsync(requestType, data, ct);

    private async Task<int?> FindSceneItemIdAsync(string scene, string source, CancellationToken ct)
    {
        try
        {
            var result = await _client.SendRequestAsync(
                "GetSceneItemId",
                new Dictionary<string, object?> { ["sceneName"] = scene, ["sourceName"] = source },
                ct).ConfigureAwait(false);

            if (!result.Success || result.Data is not { } data)
            {
                _log.Warn(Category, $"No se encontró «{source}» en la escena «{scene}».");
                return null;
            }

            return data.TryGetProperty("sceneItemId", out var id) && id.ValueKind == JsonValueKind.Number
                ? id.GetInt32()
                : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn(Category, $"No se pudo resolver «{source}» en la escena «{scene}».", ex);
            return null;
        }
    }

    private async Task<bool> RequestAsync(
        string requestType,
        IReadOnlyDictionary<string, object?>? data,
        CancellationToken ct)
    {
        try
        {
            var result = await _client.SendRequestAsync(requestType, data, ct).ConfigureAwait(false);
            if (!result.Success)
                _log.Warn(Category, $"{requestType} falló (código {result.Code}): {result.Comment ?? "sin detalle"}.");

            return result.Success;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn(Category, $"No se pudo enviar {requestType} a OBS.", ex);
            return false;
        }
    }

    private ObsSnapshot Disconnected() => new(false, null, Array.Empty<string>(), _clock.UtcNow);

    private static string? GetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
