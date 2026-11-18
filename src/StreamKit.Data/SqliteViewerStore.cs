using Microsoft.Data.Sqlite;
using StreamKit.Core.Abstractions;
using StreamKit.Core.Events;
using StreamKit.Core.Logging;

namespace StreamKit.Data;

/// <summary>
/// Puntos con caché en RAM y volcado periódico. Las lecturas de saldo no tocan disco ni
/// esperan al flush; las escrituras se agrupan en una transacción cada pocos segundos.
/// </summary>
public sealed class SqliteViewerStore : IViewerStore, IAsyncDisposable
{
    private const int MaxPendingLedgerRows = 5_000;
    private const string WatchTickReason = "tick";

    private readonly SqliteDatabase _db;
    private readonly IClock _clock;
    private readonly IAppLog _log;
    private readonly SemaphoreSlim _mutate = new(1, 1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ViewerState> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _byUnique = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<LedgerRow> _ledger = new(256);

    private readonly CancellationTokenSource _flushCts = new();
    private Task? _flushLoop;

    public SqliteViewerStore(SqliteDatabase db, IClock clock, IAppLog log)
    {
        _db = db;
        _clock = clock;
        _log = log;
    }

    public int TrackedCount => _byId.Count;

    public async Task LoadAsync(CancellationToken ct)
    {
        await _mutate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _db.WithConnectionAsync(connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT user_id, unique_id, nickname, points, lifetime_points, gifts_count, diamonds, first_seen, last_seen
                    FROM viewers;
                    """;

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var state = new ViewerState
                    {
                        UserId = reader.GetString(0),
                        UniqueId = reader.GetString(1),
                        Nickname = reader.GetString(2),
                        Points = reader.GetInt64(3),
                        LifetimePoints = reader.GetInt64(4),
                        Gifts = reader.GetInt64(5),
                        Diamonds = reader.GetInt64(6),
                        FirstSeen = ParseDate(reader.GetString(7)),
                        LastSeen = ParseDate(reader.GetString(8)),
                    };

                    _byId[state.UserId] = state;
                    _byUnique[state.UniqueId] = state.UserId;
                }

                return true;
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _mutate.Release();
        }

        _log.Info("data", $"Cacheados {_byId.Count} espectadores.");
    }

    /// <summary>Arranca el volcado periódico. Sin esto, un corte de luz pierde hasta N segundos.</summary>
    public void StartAutoFlush(TimeSpan interval)
    {
        _flushLoop ??= Task.Run(() => FlushLoopAsync(interval, _flushCts.Token));
    }

    public async Task TouchAsync(ViewerRef viewer, DateTimeOffset at, CancellationToken ct)
    {
        if (viewer.IsEmpty)
            return;

        await _mutate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = EnsureLocked(viewer, at);
            state.LastSeen = at;
            state.Nickname = viewer.DisplayName;
            state.Dirty = true;
        }
        finally
        {
            _mutate.Release();
        }
    }

    public async Task<long> AddPointsAsync(ViewerRef viewer, long delta, string reason, DateTimeOffset at, CancellationToken ct)
    {
        if (viewer.IsEmpty)
            return 0;

        await _mutate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = EnsureLocked(viewer, at);
            var before = Volatile.Read(ref state.Points);
            var after = Math.Max(0, before + delta);
            var applied = after - before;

            Volatile.Write(ref state.Points, after);
            if (applied > 0)
                state.LifetimePoints += applied;

            state.LastSeen = at;
            state.Dirty = true;

            if (applied != 0 && !string.Equals(reason, WatchTickReason, StringComparison.Ordinal))
                AddLedgerLocked(state.UserId, applied, reason, at);

            return after;
        }
        finally
        {
            _mutate.Release();
        }
    }

    public Task<long> GetPointsAsync(string userId, CancellationToken ct)
    {
        return Task.FromResult(_byId.TryGetValue(userId, out var state) ? Volatile.Read(ref state.Points) : 0L);
    }

    public async Task<ViewerBalance?> FindAsync(string uniqueIdOrUserId, CancellationToken ct)
    {
        await _mutate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = ResolveLocked(uniqueIdOrUserId);
            return state is null ? null : ToBalance(state);
        }
        finally
        {
            _mutate.Release();
        }
    }

    public async Task<IReadOnlyList<ViewerBalance>> GetTopAsync(int count, CancellationToken ct)
    {
        await _mutate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _byId.Values
                .OrderByDescending(static v => v.Points)
                .ThenBy(static v => v.UniqueId, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(1, count))
                .Select(ToBalance)
                .ToArray();
        }
        finally
        {
            _mutate.Release();
        }
    }

    public async Task<bool> TryTransferAsync(string fromUserId, string toUserId, long amount, DateTimeOffset at, CancellationToken ct)
    {
        if (amount <= 0 || string.Equals(fromUserId, toUserId, StringComparison.Ordinal))
            return false;

        await _mutate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_byId.TryGetValue(fromUserId, out var source))
                return false;

            var target = ResolveLocked(toUserId);
            if (target is null || target.UserId == source.UserId)
                return false;

            if (Volatile.Read(ref source.Points) < amount)
                return false;

            Volatile.Write(ref source.Points, source.Points - amount);
            Volatile.Write(ref target.Points, target.Points + amount);
            target.LifetimePoints += amount;
            source.LastSeen = at;
            target.LastSeen = at;
            source.Dirty = true;
            target.Dirty = true;

            AddLedgerLocked(source.UserId, -amount, "transfer:out", at);
            AddLedgerLocked(target.UserId, amount, "transfer:in", at);
            return true;
        }
        finally
        {
            _mutate.Release();
        }
    }

    public async Task<int> AwardActiveViewersAsync(int points, TimeSpan activeWindow, DateTimeOffset at, CancellationToken ct)
    {
        if (points <= 0)
            return 0;

        var cutoff = at - activeWindow;

        await _mutate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var awarded = 0;
            foreach (var state in _byId.Values)
            {
                if (state.LastSeen < cutoff)
                    continue;

                Volatile.Write(ref state.Points, state.Points + points);
                state.LifetimePoints += points;
                state.Dirty = true;
                awarded++;
            }

            return awarded;
        }
        finally
        {
            _mutate.Release();
        }
    }

    public Task<int> CountTrackedAsync(CancellationToken ct) => Task.FromResult(_byId.Count);

    public async Task RegisterGiftAsync(ViewerRef viewer, int diamonds, DateTimeOffset at, CancellationToken ct)
    {
        if (viewer.IsEmpty)
            return;

        await _mutate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = EnsureLocked(viewer, at);
            state.Gifts++;
            state.Diamonds += Math.Max(0, diamonds);
            state.LastSeen = at;
            state.Dirty = true;
        }
        finally
        {
            _mutate.Release();
        }
    }

    public async Task FlushAsync(CancellationToken ct)
    {
        await _mutate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var dirty = _byId.Values.Where(static v => v.Dirty).ToArray();
            if (dirty.Length == 0 && _ledger.Count == 0)
                return;

            var ledger = _ledger.ToArray();

            await _db.WithConnectionAsync(connection =>
            {
                using var transaction = connection.BeginTransaction();

                foreach (var state in dirty)
                {
                    using var update = connection.CreateCommand();
                    update.Transaction = transaction;
                    update.CommandText = """
                        INSERT INTO viewers (user_id, unique_id, nickname, points, lifetime_points, gifts_count, diamonds, first_seen, last_seen)
                        VALUES ($id, $unique, $nick, $points, $lifetime, $gifts, $diamonds, $first, $last)
                        ON CONFLICT(user_id) DO UPDATE SET
                          unique_id = excluded.unique_id,
                          nickname = excluded.nickname,
                          points = excluded.points,
                          lifetime_points = excluded.lifetime_points,
                          gifts_count = excluded.gifts_count,
                          diamonds = excluded.diamonds,
                          last_seen = excluded.last_seen;
                        """;

                    update.Parameters.AddWithValue("$id", state.UserId);
                    update.Parameters.AddWithValue("$unique", state.UniqueId);
                    update.Parameters.AddWithValue("$nick", state.Nickname);
                    update.Parameters.AddWithValue("$points", state.Points);
                    update.Parameters.AddWithValue("$lifetime", state.LifetimePoints);
                    update.Parameters.AddWithValue("$gifts", state.Gifts);
                    update.Parameters.AddWithValue("$diamonds", state.Diamonds);
                    update.Parameters.AddWithValue("$first", FormatDate(state.FirstSeen));
                    update.Parameters.AddWithValue("$last", FormatDate(state.LastSeen));
                    update.ExecuteNonQuery();
                }

                foreach (var row in ledger)
                {
                    using var insert = connection.CreateCommand();
                    insert.Transaction = transaction;
                    insert.CommandText =
                        "INSERT INTO points_ledger (user_id, delta, reason, created_at) VALUES ($u, $d, $r, $at);";
                    insert.Parameters.AddWithValue("$u", row.UserId);
                    insert.Parameters.AddWithValue("$d", row.Delta);
                    insert.Parameters.AddWithValue("$r", row.Reason);
                    insert.Parameters.AddWithValue("$at", FormatDate(row.At));
                    insert.ExecuteNonQuery();
                }

                transaction.Commit();
                return true;
            }, ct).ConfigureAwait(false);

            foreach (var state in dirty)
                state.Dirty = false;

            _ledger.Clear();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error("data", "Falló el volcado de puntos; se reintenta en el próximo ciclo.", ex);
        }
        finally
        {
            _mutate.Release();
        }
    }

    /// <summary>Para diagnóstico y tests: filas de auditoría efectivamente persistidas.</summary>
    public Task<long> CountLedgerRowsAsync(CancellationToken ct)
        => _db.WithConnectionAsync(
            connection => SqliteDatabase.ScalarLong(connection, "SELECT COUNT(*) FROM points_ledger;"),
            ct);

    public async ValueTask DisposeAsync()
    {
        await _flushCts.CancelAsync().ConfigureAwait(false);

        if (_flushLoop is not null)
        {
            try
            {
                await _flushLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // esperado
            }
        }

        await FlushAsync(CancellationToken.None).ConfigureAwait(false);
        _flushCts.Dispose();
        _mutate.Dispose();
    }

    private async Task FlushLoopAsync(TimeSpan interval, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                await FlushAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // apagado
        }
    }

    private ViewerState EnsureLocked(ViewerRef viewer, DateTimeOffset at)
    {
        if (_byId.TryGetValue(viewer.UserId, out var existing))
        {
            if (!string.Equals(existing.UniqueId, viewer.UniqueId, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(viewer.UniqueId))
            {
                _byUnique.Remove(existing.UniqueId);
                existing.UniqueId = viewer.UniqueId;
                _byUnique[existing.UniqueId] = existing.UserId;
            }

            return existing;
        }

        var state = new ViewerState
        {
            UserId = viewer.UserId,
            UniqueId = string.IsNullOrWhiteSpace(viewer.UniqueId) ? viewer.UserId : viewer.UniqueId,
            Nickname = viewer.DisplayName,
            FirstSeen = at,
            LastSeen = at,
        };

        _byId[state.UserId] = state;
        _byUnique[state.UniqueId] = state.UserId;
        return state;
    }

    private ViewerState? ResolveLocked(string uniqueIdOrUserId)
    {
        var key = uniqueIdOrUserId.TrimStart('@');
        if (_byUnique.TryGetValue(key, out var id) && _byId.TryGetValue(id, out var byHandle))
            return byHandle;

        return _byId.TryGetValue(key, out var byId) ? byId : null;
    }

    private void AddLedgerLocked(string userId, long delta, string reason, DateTimeOffset at)
    {
        if (_ledger.Count >= MaxPendingLedgerRows)
            _ledger.RemoveAt(0);

        _ledger.Add(new LedgerRow(userId, delta, reason, at));
    }

    private static ViewerBalance ToBalance(ViewerState state) => new(
        state.UserId,
        state.UniqueId,
        state.Nickname,
        state.Points,
        state.LifetimePoints,
        state.Gifts,
        state.Diamonds);

    private static string FormatDate(DateTimeOffset value) => value.UtcDateTime.ToString("O");

    private static DateTimeOffset ParseDate(string value)
        => DateTimeOffset.TryParse(value, out var parsed) ? parsed : DateTimeOffset.MinValue;

    private sealed class ViewerState
    {
        public required string UserId { get; init; }
        public required string UniqueId { get; set; }
        public required string Nickname { get; set; }
        public long Points;
        public long LifetimePoints;
        public long Gifts;
        public long Diamonds;
        public DateTimeOffset FirstSeen;
        public DateTimeOffset LastSeen;
        public bool Dirty;
    }

    private readonly record struct LedgerRow(string UserId, long Delta, string Reason, DateTimeOffset At);
}
