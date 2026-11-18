using StreamKit.Core.Abstractions;

namespace StreamKit.Core.Alerts;

/// <summary>
/// Agrupa ráfagas antes de encolar: likes del mismo usuario y regalos repetidos.
/// El host llama a <see cref="FlushDue"/> cada ~250 ms.
/// </summary>
public sealed class AlertCoalescer(IClock clock, Options.StreamKitOptions options)
{
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);

    public IReadOnlyList<AlertItem> Add(AlertItem item)
    {
        if (string.IsNullOrEmpty(item.MergeKey))
            return [item];

        var window = WindowFor(item.Kind);
        if (window <= TimeSpan.Zero)
            return [item];

        var now = clock.UtcNow;

        if (_pending.TryGetValue(item.MergeKey, out var existing))
        {
            existing.Item.Count += item.Count;
            existing.Item.Text = item.Text;
            existing.Item.Data["count"] = existing.Item.Count;
            existing.DueAt = now + window;
            return [];
        }

        _pending[item.MergeKey] = new Pending(item, now + window);
        return [];
    }

    public IReadOnlyList<AlertItem> FlushDue()
    {
        var now = clock.UtcNow;
        List<AlertItem>? ready = null;

        foreach (var key in _pending.Keys.ToArray())
        {
            var pending = _pending[key];
            if (pending.DueAt > now)
                continue;

            _pending.Remove(key);
            (ready ??= []).Add(pending.Item);
        }

        if (ready is null)
            return [];

        ready.Sort(static (a, b) => a.Sequence.CompareTo(b.Sequence));
        return ready;
    }

    public int PendingCount => _pending.Count;

    private TimeSpan WindowFor(AlertKind kind) => kind switch
    {
        AlertKind.Like => TimeSpan.FromMilliseconds(Math.Max(0, options.Alerts.LikeCoalesceMs)),
        AlertKind.Gift => TimeSpan.FromMilliseconds(Math.Max(0, options.Alerts.GiftCoalesceMs)),
        _ => TimeSpan.Zero,
    };

    private sealed class Pending(AlertItem item, DateTimeOffset dueAt)
    {
        public AlertItem Item { get; } = item;
        public DateTimeOffset DueAt { get; set; } = dueAt;
    }
}
