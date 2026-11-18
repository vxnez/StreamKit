namespace StreamKit.Data;

/// <summary>Pares clave/valor para estado pequeño (contadores globales, última sesión).</summary>
public sealed class KvStore(SqliteDatabase db)
{
    public Task<string?> GetAsync(string key, CancellationToken ct)
        => db.WithConnectionAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM kv WHERE key = $k LIMIT 1;";
            command.Parameters.AddWithValue("$k", key);
            return command.ExecuteScalar() as string;
        }, ct);

    public Task SetAsync(string key, string value, CancellationToken ct)
        => db.WithConnectionAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO kv (key, value) VALUES ($k, $v)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """;
            command.Parameters.AddWithValue("$k", key);
            command.Parameters.AddWithValue("$v", value);
            return command.ExecuteNonQuery();
        }, ct);
}
