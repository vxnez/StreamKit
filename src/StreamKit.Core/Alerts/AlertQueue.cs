namespace StreamKit.Core.Alerts;

public enum AlertKind
{
    Gift = 0,
    Subscribe = 1,
    Follow = 2,
    Share = 3,
    Like = 4,
    Custom = 5,
}

/// <summary>Unidad de la cola de overlays. Mutable solo para coalescer ráfagas antes de encolarse.</summary>
public sealed class AlertItem
{
    public required string Id { get; init; }

    public required AlertKind Kind { get; init; }

    /// <summary>Mayor gana. Ver <see cref="AlertPriority"/>.</summary>
    public required int Priority { get; init; }

    public required string Text { get; set; }

    public int DurationMs { get; set; }

    /// <summary>Momento en que se creó la alerta; viaja al overlay como <c>ts</c> para medir latencia.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public int Count { get; set; } = 1;

    /// <summary>Clave de coalescencia (usuario+regalo, por ejemplo). Vacía = nunca se agrupa.</summary>
    public string MergeKey { get; init; } = string.Empty;

    public Dictionary<string, object?> Data { get; init; } = new();

    public long Sequence { get; set; }

    public static string NewId() => Guid.NewGuid().ToString("N")[..8];
}

public static class AlertPriority
{
    public const int Gift = 100;
    public const int Subscribe = 90;
    public const int Follow = 80;
    public const int Share = 60;
    public const int Like = 40;
    public const int Custom = 50;
}

/// <summary>
/// Cola acotada con prioridad y descarte explícito. N es chico (&lt;= 1024), así que el
/// recorrido lineal por dequeue es más simple y predecible que un heap con remociones.
/// </summary>
public sealed class AlertQueue(int maxDepth)
{
    private readonly List<AlertItem> _items = new(Math.Min(maxDepth, 64));
    private readonly object _gate = new();
    private long _sequence;

    public int Capacity { get; } = Math.Max(1, maxDepth);

    public int Count
    {
        get
        {
            lock (_gate)
                return _items.Count;
        }
    }

    public long Dropped { get; private set; }

    public long NextSequence() => Interlocked.Increment(ref _sequence);

    /// <summary>Encola. Si está llena, desplaza el ítem de menor prioridad solo si el nuevo es mejor.</summary>
    public bool TryEnqueue(AlertItem item)
    {
        lock (_gate)
        {
            if (_items.Count >= Capacity)
            {
                var worstIndex = IndexOfWorst();
                var worst = _items[worstIndex];

                if (item.Priority <= worst.Priority)
                {
                    Dropped++;
                    return false;
                }

                _items.RemoveAt(worstIndex);
                Dropped++;
            }

            _items.Add(item);
            return true;
        }
    }

    /// <summary>Devuelve el mejor ítem (mayor prioridad, más antiguo) y lo saca de la cola.</summary>
    public AlertItem? Dequeue()
    {
        lock (_gate)
        {
            if (_items.Count == 0)
                return null;

            var bestIndex = 0;
            for (var i = 1; i < _items.Count; i++)
            {
                if (IsBetter(_items[i], _items[bestIndex]))
                    bestIndex = i;
            }

            var item = _items[bestIndex];
            _items.RemoveAt(bestIndex);
            return item;
        }
    }

    public AlertItem? Peek()
    {
        lock (_gate)
        {
            if (_items.Count == 0)
                return null;

            var best = _items[0];
            for (var i = 1; i < _items.Count; i++)
            {
                if (IsBetter(_items[i], best))
                    best = _items[i];
            }

            return best;
        }
    }

    public void Clear()
    {
        lock (_gate)
            _items.Clear();
    }

    private int IndexOfWorst()
    {
        var worst = 0;
        for (var i = 1; i < _items.Count; i++)
        {
            if (IsBetter(_items[worst], _items[i]))
                worst = i;
        }

        return worst;
    }

    private static bool IsBetter(AlertItem candidate, AlertItem current)
        => candidate.Priority > current.Priority
           || (candidate.Priority == current.Priority && candidate.Sequence < current.Sequence);
}
