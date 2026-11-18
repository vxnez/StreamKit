using System.Collections.Concurrent;
using StreamKit.Core.Abstractions;

namespace StreamKit.Core.Commands;

/// <summary>
/// Ventanas de espera en RAM. Una entrada por clave; las claves vencidas se podan en lote
/// para que un chat ruidoso no haga crecer el diccionario sin límite.
/// </summary>
public sealed class CooldownTracker(IClock clock)
{
    private const int PruneEvery = 512;
    private const int HardLimit = 100_000;

    private readonly ConcurrentDictionary<string, long> _nextAllowedTicks = new(StringComparer.Ordinal);
    private int _operations;

    public int Count => _nextAllowedTicks.Count;

    /// <summary>Devuelve true y arma la ventana si la clave estaba libre o vencida.</summary>
    public bool TryAcquire(string key, TimeSpan window)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (window <= TimeSpan.Zero)
            return true;

        var now = clock.UtcNow.UtcTicks;
        var next = now + window.Ticks;

        while (true)
        {
            if (_nextAllowedTicks.TryGetValue(key, out var existing))
            {
                if (existing > now)
                    return false;

                if (_nextAllowedTicks.TryUpdate(key, next, existing))
                {
                    PruneIfNeeded(now);
                    return true;
                }
            }
            else if (_nextAllowedTicks.TryAdd(key, next))
            {
                PruneIfNeeded(now);
                return true;
            }
        }
    }

    public TimeSpan? Remaining(string key)
    {
        if (!_nextAllowedTicks.TryGetValue(key, out var next))
            return null;

        var remaining = next - clock.UtcNow.UtcTicks;
        return remaining <= 0 ? null : TimeSpan.FromTicks(remaining);
    }

    public void Clear() => _nextAllowedTicks.Clear();

    private void PruneIfNeeded(long now)
    {
        if (Interlocked.Increment(ref _operations) % PruneEvery != 0)
            return;

        foreach (var pair in _nextAllowedTicks)
        {
            if (pair.Value <= now)
                _nextAllowedTicks.TryRemove(pair);
        }

        if (_nextAllowedTicks.Count > HardLimit)
            _nextAllowedTicks.Clear();
    }
}
