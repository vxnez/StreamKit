using StreamKit.Core.Alerts;
using StreamKit.Core.Events;

namespace StreamKit.Core.Hosting;

/// <summary>
/// Salidas visibles del directo (overlays). La implementación concreta vive en la capa web; el
/// dominio solo conoce este contrato, así que el pipeline se prueba con un doble en memoria.
/// </summary>
public interface ILiveOutput
{
    void EnqueueAlert(AlertItem item);

    ValueTask ShowChatAsync(ChatEvent chat, CancellationToken ct);

    ValueTask ShowBotMessageAsync(string text, bool isError, CancellationToken ct);
}
