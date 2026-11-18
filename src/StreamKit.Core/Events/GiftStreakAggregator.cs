using StreamKit.Core.Abstractions;

namespace StreamKit.Core.Events;

/// <summary>
/// Une los mensajes repetidos de una ráfaga de regalos en un solo <see cref="GiftEvent"/>.
/// TikTok manda un mensaje por cada unidad; sin esto se abonaría de más y se encolarían N alertas.
/// </summary>
public sealed class GiftStreakAggregator(IClock clock)
{
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);

    public GiftEvent? Add(GiftEvent gift)
    {
        var key = $"{gift.Viewer.UserId}|{gift.GiftId}";
        var count = Math.Max(1, gift.Count);

        if (_pending.TryGetValue(key, out var pending))
        {
            pending.Count += count;
            pending.UnitDiamonds = Math.Max(pending.UnitDiamonds, gift.Diamonds);
            pending.LastSeen = clock.UtcNow;

            if (!gift.StreakFinished)
                return null;

            _pending.Remove(key);
            return pending.ToEvent(gift);
        }

        if (gift.StreakFinished)
            return gift;

        _pending[key] = new Pending
        {
            Viewer = gift.Viewer,
            GiftId = gift.GiftId,
            GiftName = gift.GiftName,
            Count = count,
            UnitDiamonds = gift.Diamonds,
            StartedAt = clock.UtcNow,
            LastSeen = clock.UtcNow,
        };

        return null;
    }

    /// <summary>Emite ráfagas que dejaron de llegar sin mensaje de cierre (fin de stream, red).</summary>
    public IReadOnlyList<GiftEvent> FlushIdle(TimeSpan idleFor)
    {
        if (_pending.Count == 0)
            return [];

        var cutoff = clock.UtcNow - idleFor;
        List<GiftEvent>? ready = null;

        foreach (var key in _pending.Keys.ToArray())
        {
            var pending = _pending[key];
            if (pending.LastSeen > cutoff)
                continue;

            _pending.Remove(key);
            (ready ??= []).Add(pending.ToEvent(pending.LastSeen));
        }

        return ready ?? [];
    }

    public int PendingCount => _pending.Count;

    private sealed class Pending
    {
        public required ViewerRef Viewer { get; init; }
        public required string GiftId { get; init; }
        public required string GiftName { get; init; }
        public int Count { get; set; }
        public int UnitDiamonds { get; set; }
        public DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset LastSeen { get; set; }

        public GiftEvent ToEvent(GiftEvent source) => new(
            source.At,
            Viewer,
            GiftId,
            GiftName,
            Count,
            UnitDiamonds,
            StreakFinished: true);

        public GiftEvent ToEvent(DateTimeOffset at) => new(
            at,
            Viewer,
            GiftId,
            GiftName,
            Count,
            UnitDiamonds,
            StreakFinished: true);
    }
}
