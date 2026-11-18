using System.Threading.Channels;
using StreamKit.Core.Events;
using StreamKit.Core.Logging;

namespace StreamKit.Core.Events;

public interface IStreamEventHandler
{
    ValueTask HandleAsync(StreamEvent evt, CancellationToken ct);
}

/// <summary>
/// Consumidor único del canal de eventos. Secuencial a propósito: orden estable y cero
/// contención. Cada handler se aísla para que un fallo no corte el pipeline.
/// </summary>
public sealed class EventPipeline(
    IEnumerable<IStreamEventHandler> handlers,
    State.RuntimeState state,
    Logging.IAppLog log)
{
    private readonly IStreamEventHandler[] _handlers = handlers.ToArray();

    public async Task RunAsync(ChannelReader<StreamEvent> reader, CancellationToken ct)
    {
        try
        {
            await foreach (var evt in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                foreach (var handler in _handlers)
                {
                    try
                    {
                        await handler.HandleAsync(evt, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        log.Error("pipeline", $"Handler {handler.GetType().Name} falló con {evt.Kind}.", ex);
                    }
                }

                state.MarkEvent();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Apagado normal.
        }
    }
}
