using MySqlConnector;

namespace Astra.Shared.Configuration;

public sealed class DatabaseSettings
{
    public string? ConnectionString { get; set; }
    public string GetConnectionString()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
            throw new InvalidOperationException("MySQL is not configured.");
        return new MySqlConnectionStringBuilder(ConnectionString)
        {
            ApplicationName = "ASTRA .NET API",
            MaximumPoolSize = 10,
            ConnectionTimeout = 10,
            GuidFormat = MySqlGuidFormat.Char36,
            DateTimeKind = MySqlDateTimeKind.Utc,
            TreatTinyAsBoolean = true,
            // Callers depend on matched-row counts for unchanged updates.
            UseAffectedRows = false
        }.ConnectionString;
    }
}
