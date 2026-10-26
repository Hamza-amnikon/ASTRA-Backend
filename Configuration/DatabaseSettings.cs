using Microsoft.Data.SqlClient;

namespace Astra.Api.Configuration;

public sealed class DatabaseSettings
{
    public string? ConnectionString { get; set; }

    public string GetConnectionString()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
            throw new InvalidOperationException("SQL Server is not configured.");

        return new SqlConnectionStringBuilder(ConnectionString)
        {
            ApplicationName = "ASTRA .NET API",
            MaxPoolSize = 10,
            ConnectTimeout = 10
        }.ConnectionString;
    }
}
