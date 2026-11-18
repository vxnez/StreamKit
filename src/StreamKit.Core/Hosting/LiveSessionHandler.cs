using StreamKit.Core.Abstractions;
using StreamKit.Core.Alerts;
using StreamKit.Core.Commands;
using StreamKit.Core.Events;
using StreamKit.Core.Options;
using StreamKit.Core.Points;

namespace StreamKit.Core.Hosting;

/// <summary>
/// Corazón del directo: abona puntos, responde comandos y encola alertas. Se ejecuta en el
/// pipeline (un solo hilo) y no bloquea: las alertas pasan por el coalescedor y las respuestas
/// se publican en el overlay de chat, porque TikTok no permite escribir en el chat.
/// </summary>
public sealed class LiveSessionHandler : IStreamEventHandler
{
    private readonly PointsEngine _points;
    private readonly CommandRouter _commands;
    private readonly GiftStreakAggregator _gifts;
    private readonly AlertCoalescer _coalescer;
    private readonly ILiveOutput _output;
    private readonly StreamKitOptions _options;

    public LiveSessionHandler(
        PointsEngine points,
        CommandRouter commands,
        GiftStreakAggregator gifts,
        AlertCoalescer coalescer,
        ILiveOutput output,
        StreamKitOptions options)
    {
        _points = points ?? throw new ArgumentNullException(nameof(points));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _gifts = gifts ?? throw new ArgumentNullException(nameof(gifts));
        _coalescer = coalescer ?? throw new ArgumentNullException(nameof(coalescer));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async ValueTask HandleAsync(StreamEvent evt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(evt);

        switch (evt)
        {
            case GiftEvent gift:
                await HandleGiftAsync(gift, ct).ConfigureAwait(false);
                break;

            case ChatEvent chat:
                await HandleChatAsync(chat, ct).ConfigureAwait(false);
                break;

            case StreamEndedEvent:
                await FlushGiftsAsync(TimeSpan.Zero, ct).ConfigureAwait(false);
                break;

            default:
                Enqueue(LiveAlerts.From(evt, _options));
                break;
        }
    }

    /// <summary>Publica las alertas que el coalescedor ya dio por cerradas. Lo llama un tick de 250 ms.</summary>
    public void FlushAlerts()
    {
        foreach (var item in _coalescer.FlushDue())
            _output.EnqueueAlert(item);
    }

    /// <summary>Emite ráfagas de regalos que nunca recibieron el mensaje de cierre.</summary>
    public async Task FlushGiftsAsync(TimeSpan idleFor, CancellationToken ct)
    {
        foreach (var gift in _gifts.FlushIdle(idleFor))
        {
            await _points.AwardForGiftAsync(gift, ct).ConfigureAwait(false);
            Enqueue(LiveAlerts.From(gift, _options));
        }
    }

    private async Task HandleGiftAsync(GiftEvent gift, CancellationToken ct)
    {
        // La plataforma repite el evento por cada unidad de una ráfaga: primero se agrupa.
        var merged = _gifts.Add(gift);
        if (merged is null)
            return;

        await _points.AwardForGiftAsync(merged, ct).ConfigureAwait(false);
        Enqueue(LiveAlerts.From(merged, _options));
    }

    private async Task HandleChatAsync(ChatEvent chat, CancellationToken ct)
    {
        await _output.ShowChatAsync(chat, ct).ConfigureAwait(false);

        await _points.AwardForChatAsync(chat, ct).ConfigureAwait(false);

        var response = await _commands.HandleAsync(chat, ct).ConfigureAwait(false);
        if (response is not null)
            await _output.ShowBotMessageAsync(response.Text, response.IsError, ct).ConfigureAwait(false);
    }

    private void Enqueue(AlertItem? item)
    {
        if (item is null)
            return;

        // El coalescedor devuelve vacío mientras la ráfaga sigue abierta; FlushAlerts la publica.
        foreach (var ready in _coalescer.Add(item))
            _output.EnqueueAlert(ready);
    }
}
