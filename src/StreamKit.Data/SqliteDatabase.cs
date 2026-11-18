using Microsoft.Data.Sqlite;

namespace StreamKit.Data;

/// <summary>
/// Dueño de una única conexión de larga vida. SQLite es síncrono por naturaleza: las
/// llamadas son cortas y locales, así que se ejecutan dentro de un gate para serializar
/// escritores sin pool ni contención.
/// </summary>
public sealed class SqliteDatabase : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _connection;

    public SqliteDatabase(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path;
    }

    public string Path { get; }

    public async Task InitializeAsync(CancellationToken ct)
    {
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            Pooling = false,
            DefaultTimeout = 5,
        };

        var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(ct).ConfigureAwait(false);

        Execute(connection, "PRAGMA journal_mode = WAL;");
        Execute(connection, "PRAGMA synchronous = NORMAL;");
        Execute(connection, "PRAGMA busy_timeout = 5000;");
        Execute(connection, "PRAGMA foreign_keys = ON;");
        Execute(connection, "PRAGMA temp_store = MEMORY;");
        Execute(connection, "PRAGMA cache_size = -8000;");

        Migrations.Apply(connection);
        _connection = connection;
    }

    public async Task<T> WithConnectionAsync<T>(Func<SqliteConnection, T> work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return work(_connection ?? throw new InvalidOperationException("La base no fue inicializada."));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_connection is not null)
            {
                Execute(_connection, "PRAGMA wal_checkpoint(TRUNCATE);");
                await _connection.DisposeAsync().ConfigureAwait(false);
                _connection = null;
            }
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    internal static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        command.ExecuteNonQuery();
    }

    internal static long ScalarLong(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        var value = command.ExecuteScalar();
        return value is null or DBNull ? 0 : Convert.ToInt64(value);
    }
}
