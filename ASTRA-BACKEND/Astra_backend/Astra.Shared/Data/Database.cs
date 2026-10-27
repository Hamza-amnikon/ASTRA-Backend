using MySqlConnector;

namespace Astra.Shared.Data;

public sealed class Database(Microsoft.Extensions.Options.IOptions<DatabaseSettings> settings)
{
    public async Task<MySqlConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new MySqlConnection(settings.Value.GetConnectionString());
        try { await connection.OpenAsync(cancellationToken); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }

    public static async Task<List<Dictionary<string, object?>>> QueryAsync(MySqlConnection connection, string sql,
        IReadOnlyDictionary<string, object?>? values = null, MySqlTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        await using var command = Command(connection, sql, values, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<Dictionary<string, object?>>();
        do
        {
            if (reader.FieldCount == 0) continue;
            while (await reader.ReadAsync(cancellationToken))
            {
                var row = new Dictionary<string, object?>(StringComparer.Ordinal);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    object? value = await reader.IsDBNullAsync(i, cancellationToken) ? null : reader.GetValue(i);
                    // MySQL DATETIME(6) holds UTC instants; DATE holds calendar dates.
                    if (value is DateTime instant && reader.GetDataTypeName(i).StartsWith("DATETIME", StringComparison.OrdinalIgnoreCase))
                        value = new DateTimeOffset(DateTime.SpecifyKind(instant, DateTimeKind.Utc));
                    row[reader.GetName(i)] = value;
                }
                result.Add(row);
            }
        } while (await reader.NextResultAsync(cancellationToken));
        return result;
    }

    public static async Task<int> ExecuteAsync(MySqlConnection connection, string sql,
        IReadOnlyDictionary<string, object?>? values = null, MySqlTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        await using var command = Command(connection, sql, values, transaction);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static MySqlCommand Command(MySqlConnection connection, string sql,
        IReadOnlyDictionary<string, object?>? values, MySqlTransaction? transaction)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 30;
        command.Transaction = transaction;
        foreach (var (name, value) in values ?? new Dictionary<string, object?>())
            command.Parameters.AddWithValue(name.StartsWith('@') ? name : "@" + name,
                value is DateTimeOffset instant ? instant.UtcDateTime : value ?? DBNull.Value);
        return command;
    }
}
