using StreamKit.Core.Events;
using StreamKit.Core.Logging;
using StreamKit.Data;
using Xunit;

namespace StreamKit.Tests;

public sealed class SqliteStoreTests : IAsyncLifetime
{
    private static readonly ViewerRef Ana = new("100", "ana", "Ana");
    private static readonly ViewerRef Beto = new("200", "beto", "Beto");

    private readonly FakeClock _clock = new();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "streamkit-tests", Guid.NewGuid().ToString("N"));
    private string _dbPath = string.Empty;
    private SqliteDatabase _db = null!;
    private SqliteViewerStore _store = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _dbPath = Path.Combine(_directory, "streamkit.db");
        await OpenAsync();
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        await _db.DisposeAsync();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Windows puede retener el handle un instante; el directorio es temporal.
        }
    }

    [Fact]
    public async Task PersistsPointsAcrossRestart()
    {
        await _store.AddPointsAsync(Ana, 10, "seed", _clock.UtcNow, CancellationToken.None);
        await _store.FlushAsync(CancellationToken.None);

        await ReopenAsync();

        Assert.Equal(10, await _store.GetPointsAsync(Ana.UserId, CancellationToken.None));
        Assert.Equal(1, await _store.CountTrackedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TransferMovesPointsAndPersists()
    {
        await _store.AddPointsAsync(Ana, 100, "seed", _clock.UtcNow, CancellationToken.None);
        await _store.TouchAsync(Beto, _clock.UtcNow, CancellationToken.None);

        Assert.True(await _store.TryTransferAsync(Ana.UserId, "beto", 30, _clock.UtcNow, CancellationToken.None));
        await _store.FlushAsync(CancellationToken.None);

        await ReopenAsync();

        Assert.Equal(70, await _store.GetPointsAsync(Ana.UserId, CancellationToken.None));
        Assert.Equal(30, await _store.GetPointsAsync(Beto.UserId, CancellationToken.None));
    }

    [Fact]
    public async Task TransferRejectsInsufficientFunds()
    {
        await _store.TouchAsync(Ana, _clock.UtcNow, CancellationToken.None);
        await _store.TouchAsync(Beto, _clock.UtcNow, CancellationToken.None);

        Assert.False(await _store.TryTransferAsync(Ana.UserId, "beto", 10, _clock.UtcNow, CancellationToken.None));
    }

    [Fact]
    public async Task TopIsOrderedByPoints()
    {
        await _store.AddPointsAsync(Ana, 10, "seed", _clock.UtcNow, CancellationToken.None);
        await _store.AddPointsAsync(Beto, 40, "seed", _clock.UtcNow, CancellationToken.None);

        var top = await _store.GetTopAsync(2, CancellationToken.None);

        Assert.Equal(2, top.Count);
        Assert.Equal("beto", top[0].UniqueId);
        Assert.Equal("ana", top[1].UniqueId);
    }

    [Fact]
    public async Task WatchTimeOnlyAwardsRecentlySeenViewers()
    {
        await _store.TouchAsync(Ana, _clock.UtcNow, CancellationToken.None);
        await _store.TouchAsync(Beto, _clock.UtcNow, CancellationToken.None);

        _clock.Advance(TimeSpan.FromMinutes(11));
        await _store.TouchAsync(Beto, _clock.UtcNow, CancellationToken.None);

        var awarded = await _store.AwardActiveViewersAsync(
            1,
            TimeSpan.FromMinutes(10),
            _clock.UtcNow,
            CancellationToken.None);

        Assert.Equal(1, awarded);
        Assert.Equal(0, await _store.GetPointsAsync(Ana.UserId, CancellationToken.None));
        Assert.Equal(1, await _store.GetPointsAsync(Beto.UserId, CancellationToken.None));
    }

    [Fact]
    public async Task LedgerOnlyRecordsAuditableMovements()
    {
        await _store.AddPointsAsync(Ana, 5, "chat", _clock.UtcNow, CancellationToken.None);
        await _store.AddPointsAsync(Ana, 1, "tick", _clock.UtcNow, CancellationToken.None);
        await _store.FlushAsync(CancellationToken.None);

        Assert.Equal(1, await _store.CountLedgerRowsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task NegativeDeltaClampsAtZero()
    {
        await _store.AddPointsAsync(Ana, 5, "seed", _clock.UtcNow, CancellationToken.None);
        var balance = await _store.AddPointsAsync(Ana, -50, "manual:test", _clock.UtcNow, CancellationToken.None);

        Assert.Equal(0, balance);
        Assert.Equal(0, await _store.GetPointsAsync(Ana.UserId, CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentAwardsDoNotLosePoints()
    {
        await Task.WhenAll(Enumerable.Range(0, 100).Select(_ =>
            _store.AddPointsAsync(Ana, 1, "chat", _clock.UtcNow, CancellationToken.None)));

        Assert.Equal(100, await _store.GetPointsAsync(Ana.UserId, CancellationToken.None));
    }

    [Fact]
    public async Task RegisterGiftUpdatesCounters()
    {
        await _store.RegisterGiftAsync(Ana, 10, _clock.UtcNow, CancellationToken.None);
        await _store.RegisterGiftAsync(Ana, 5, _clock.UtcNow, CancellationToken.None);

        var viewer = await _store.FindAsync("ana", CancellationToken.None);

        Assert.NotNull(viewer);
        Assert.Equal(2, viewer!.Gifts);
        Assert.Equal(15, viewer.Diamonds);
    }

    [Fact]
    public async Task KvStoreRoundTrips()
    {
        var kv = new KvStore(_db);
        await kv.SetAsync("total", "42", CancellationToken.None);

        Assert.Equal("42", await kv.GetAsync("total", CancellationToken.None));
        Assert.Null(await kv.GetAsync("missing", CancellationToken.None));
    }

    private async Task OpenAsync()
    {
        _db = new SqliteDatabase(_dbPath);
        await _db.InitializeAsync(CancellationToken.None);
        _store = new SqliteViewerStore(_db, _clock, NullAppLog.Instance);
        await _store.LoadAsync(CancellationToken.None);
    }

    private async Task ReopenAsync()
    {
        await _store.DisposeAsync();
        await _db.DisposeAsync();
        await OpenAsync();
    }
}
