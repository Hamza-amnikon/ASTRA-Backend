using MySqlConnector;

public sealed class FeatureSchemaStartup(MySqlStore store, IConfiguration config) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.GetConnectionString("MySql")))
            return;
        await store.EnsureFeatureStorage(ct);
    }
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
public sealed partial class MySqlStore
{
    public async Task EnsureFeatureStorage(CancellationToken ct)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        foreach (var sql in (await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "feature-runtime.sql"), ct)).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            await using var command = new MySqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(ct);
        }
    }
}
