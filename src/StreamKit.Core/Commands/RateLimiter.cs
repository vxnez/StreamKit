using System.Collections.Concurrent;
using StreamKit.Core.Abstractions;

namespace StreamKit.Core.Commands;

/// <summary>Ventana fija por clave: N eventos cada X tiempo. Para anti-spam, no para cooldowns.</summary>
public sealed class RateLimiter(IClock clock)
{
    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);

    public bool TryAcquire(string key, int max, TimeSpan window)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (max <= 0)
            return false;

        var now = clock.UtcNow.UtcTicks;
        var state = _windows.GetOrAdd(key, static _ => new Window());

        lock (state)
        {
            if (state.ResetAtTicks <= now)
            {
                state.ResetAtTicks = now + window.Ticks;
                state.Count = 1;
                return true;
            }

            if (state.Count >= max)
                return false;

            state.Count++;
            return true;
        }
    }

    public void Clear() => _windows.Clear();

    private sealed class Window
    {
        public long ResetAtTicks;
        public int Count;
    }
}
