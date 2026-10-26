using System.Data;
using Microsoft.Data.SqlClient;

namespace Astra.Api.Data;

public sealed class Database(Microsoft.Extensions.Options.IOptions<DatabaseSettings> settings)
{
    public async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqlConnection(settings.Value.GetConnectionString());
        try { await connection.OpenAsync(cancellationToken); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }

    public static async Task<List<Dictionary<string, object?>>> QueryAsync(SqlConnection connection, string sql, IReadOnlyDictionary<string, object?>? values = null, SqlTransaction? transaction = null, CancellationToken cancellationToken = default)
    {
        await using var command = Command(connection, sql, values, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = await reader.IsDBNullAsync(i, cancellationToken) ? null : reader.GetValue(i);
            result.Add(row);
        }
        return result;
    }

    public static async Task<int> ExecuteAsync(SqlConnection connection, string sql, IReadOnlyDictionary<string, object?>? values = null, SqlTransaction? transaction = null, CancellationToken cancellationToken = default)
    {
        await using var command = Command(connection, sql, values, transaction);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static SqlCommand Command(SqlConnection connection, string sql, IReadOnlyDictionary<string, object?>? values, SqlTransaction? transaction)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 30;
        command.Transaction = transaction;
        foreach (var (name, value) in values ?? new Dictionary<string, object?>())
        {
            var parameter = new SqlParameter(name.StartsWith('@') ? name : "@" + name, value ?? DBNull.Value);
            if (value is string) { parameter.SqlDbType = SqlDbType.NVarChar; parameter.Size = -1; }
            if (value is Guid) parameter.SqlDbType = SqlDbType.UniqueIdentifier;
            if (value is DateTimeOffset) parameter.SqlDbType = SqlDbType.DateTimeOffset;
            command.Parameters.Add(parameter);
        }
        return command;
    }
}
