using Microsoft.Data.Sqlite;

namespace StreamKit.Data;

internal static class Migrations
{
    public const int CurrentVersion = 1;

    public static void Apply(SqliteConnection connection)
    {
        SqliteDatabase.Execute(connection, "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL);");
        var version = (int)SqliteDatabase.ScalarLong(connection, "SELECT COALESCE(MAX(version), 0) FROM schema_version;");

        if (version < 1)
            ApplyV1(connection);

        if (version < CurrentVersion)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO schema_version (version) VALUES ($v);";
            command.Parameters.AddWithValue("$v", CurrentVersion);
            command.ExecuteNonQuery();
        }
    }

    private static void ApplyV1(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();

        SqliteDatabase.Execute(connection, """
            CREATE TABLE IF NOT EXISTS viewers (
              user_id         TEXT PRIMARY KEY,
              unique_id       TEXT NOT NULL,
              nickname        TEXT NOT NULL,
              points          INTEGER NOT NULL DEFAULT 0,
              lifetime_points INTEGER NOT NULL DEFAULT 0,
              gifts_count     INTEGER NOT NULL DEFAULT 0,
              diamonds        INTEGER NOT NULL DEFAULT 0,
              first_seen      TEXT NOT NULL,
              last_seen       TEXT NOT NULL
            );
            """, transaction);

        SqliteDatabase.Execute(connection,
            "CREATE UNIQUE INDEX IF NOT EXISTS ux_viewers_unique_id ON viewers(unique_id);", transaction);
        SqliteDatabase.Execute(connection,
            "CREATE INDEX IF NOT EXISTS ix_viewers_points ON viewers(points DESC);", transaction);

        SqliteDatabase.Execute(connection, """
            CREATE TABLE IF NOT EXISTS points_ledger (
              id         INTEGER PRIMARY KEY AUTOINCREMENT,
              user_id    TEXT NOT NULL,
              delta      INTEGER NOT NULL,
              reason     TEXT NOT NULL,
              created_at TEXT NOT NULL
            );
            """, transaction);

        SqliteDatabase.Execute(connection,
            "CREATE INDEX IF NOT EXISTS ix_ledger_user ON points_ledger(user_id, created_at DESC);", transaction);

        SqliteDatabase.Execute(connection,
            "CREATE TABLE IF NOT EXISTS kv (key TEXT PRIMARY KEY, value TEXT NOT NULL);", transaction);

        transaction.Commit();
    }
}
