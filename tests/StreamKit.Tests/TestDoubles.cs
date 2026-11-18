using StreamKit.Core.Abstractions;
using StreamKit.Core.Events;

namespace StreamKit.Tests;

internal sealed class FakeClock : IClock
{
    public FakeClock(DateTimeOffset? start = null)
    {
        UtcNow = start ?? new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    }

    public DateTimeOffset UtcNow { get; private set; }

    public void Advance(TimeSpan by) => UtcNow += by;

    public void AdvanceSeconds(double seconds) => Advance(TimeSpan.FromSeconds(seconds));
}

internal sealed class InMemoryViewerStore : IViewerStore
{
    private readonly Dictionary<string, ViewerBalance> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _byUnique = new(StringComparer.OrdinalIgnoreCase);

    public int FlushCount { get; private set; }

    public IReadOnlyCollection<ViewerBalance> All => _byId.Values;

    public Task TouchAsync(ViewerRef viewer, DateTimeOffset at, CancellationToken ct)
    {
        Ensure(viewer);
        return Task.CompletedTask;
    }

    public Task<long> AddPointsAsync(ViewerRef viewer, long delta, string reason, DateTimeOffset at, CancellationToken ct)
    {
        var balance = Ensure(viewer);
        var updated = balance with
        {
            Points = balance.Points + delta,
            LifetimePoints = balance.LifetimePoints + Math.Max(0, delta),
        };

        _byId[updated.UserId] = updated;
        return Task.FromResult(updated.Points);
    }

    public Task<long> GetPointsAsync(string userId, CancellationToken ct)
        => Task.FromResult(_byId.TryGetValue(userId, out var balance) ? balance.Points : 0);

    public Task<ViewerBalance?> FindAsync(string uniqueIdOrUserId, CancellationToken ct)
    {
        var key = uniqueIdOrUserId.TrimStart('@');
        if (_byUnique.TryGetValue(key, out var id) && _byId.TryGetValue(id, out var byHandle))
            return Task.FromResult<ViewerBalance?>(byHandle);

        return Task.FromResult(_byId.TryGetValue(key, out var byId) ? byId : null);
    }

    public Task<IReadOnlyList<ViewerBalance>> GetTopAsync(int count, CancellationToken ct)
    {
        IReadOnlyList<ViewerBalance> top = _byId.Values
            .OrderByDescending(v => v.Points)
            .ThenBy(v => v.UniqueId, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, count))
            .ToArray();

        return Task.FromResult(top);
    }

    public Task<bool> TryTransferAsync(string fromUserId, string toUserId, long amount, DateTimeOffset at, CancellationToken ct)
    {
        if (amount <= 0)
            return Task.FromResult(false);

        var target = Resolve(toUserId);
        if (!_byId.TryGetValue(fromUserId, out var source) || target is null)
            return Task.FromResult(false);

        if (source.Points < amount)
            return Task.FromResult(false);

        if (source.UserId == target.UserId)
            return Task.FromResult(false);

        _byId[source.UserId] = source with { Points = source.Points - amount };
        _byId[target.UserId] = target with
        {
            Points = target.Points + amount,
            LifetimePoints = target.LifetimePoints + amount,
        };

        return Task.FromResult(true);
    }

    public Task<int> AwardActiveViewersAsync(int points, TimeSpan activeWindow, DateTimeOffset at, CancellationToken ct)
    {
        foreach (var pair in _byId.ToArray())
        {
            _byId[pair.Key] = pair.Value with
            {
                Points = pair.Value.Points + points,
                LifetimePoints = pair.Value.LifetimePoints + points,
            };
        }

        return Task.FromResult(_byId.Count);
    }

    public Task<int> CountTrackedAsync(CancellationToken ct) => Task.FromResult(_byId.Count);

    public Task RegisterGiftAsync(ViewerRef viewer, int diamonds, DateTimeOffset at, CancellationToken ct)
    {
        if (viewer.IsEmpty)
            return Task.CompletedTask;

        var balance = Ensure(viewer);
        _byId[balance.UserId] = balance with
        {
            Gifts = balance.Gifts + 1,
            Diamonds = balance.Diamonds + Math.Max(0, diamonds),
        };

        return Task.CompletedTask;
    }

    public Task FlushAsync(CancellationToken ct)
    {
        FlushCount++;
        return Task.CompletedTask;
    }

    private ViewerBalance Ensure(ViewerRef viewer)
    {
        if (_byId.TryGetValue(viewer.UserId, out var existing))
            return existing;

        var balance = new ViewerBalance(
            viewer.UserId,
            string.IsNullOrWhiteSpace(viewer.UniqueId) ? viewer.UserId : viewer.UniqueId,
            viewer.DisplayName,
            0,
            0,
            0,
            0);

        _byId[balance.UserId] = balance;
        _byUnique[balance.UniqueId] = balance.UserId;
        return balance;
    }

    private ViewerBalance? Resolve(string uniqueIdOrUserId)
    {
        var key = uniqueIdOrUserId.TrimStart('@');
        if (_byUnique.TryGetValue(key, out var id) && _byId.TryGetValue(id, out var byHandle))
            return byHandle;

        return _byId.TryGetValue(key, out var byId) ? byId : null;
    }
}
