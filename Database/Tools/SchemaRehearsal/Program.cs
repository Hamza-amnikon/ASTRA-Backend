using System.Text;
using MySqlConnector;

var root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
var databaseRoot = Directory.Exists(Path.Combine(root, "Database"))
    ? Path.Combine(root, "Database") : Path.Combine(root, "apps", "api", "Database");
var options = Astra.Tools.RehearsalConnection.Load(root);
await using var connection = new MySqlConnection(options.ConnectionString);
var database = "astra_rehearsal_" + Guid.NewGuid().ToString("N");
var created = false;
try
{
    await connection.OpenAsync();
    await Execute($"CREATE DATABASE `{database}` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_as_cs");
    created = true;
    await connection.ChangeDatabaseAsync(database);
    foreach (var relative in new[] { "Migrations/001_source_schema.sql", "Migrations/002_source_behaviors.sql", "Tools/mysql-rehearsal-tests.sql" })
    {
        var count = 0;
        foreach (var statement in ParseStatements(Path.Combine(databaseRoot, relative)))
        {
            count++;
            try { await Execute(statement); }
            catch (MySqlException exception)
            {
                Console.Error.WriteLine($"{relative}, statement {count}, MySQL error {exception.Number}: {exception.Message}");
                Console.Error.WriteLine(statement.Split('\n')[0]);
                throw new InvalidOperationException("Schema rehearsal failed; source data was not accessed.");
            }
        }
        Console.WriteLine($"{relative}: {count} statements passed");
    }
    await using var tables = new MySqlCommand("SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_type = 'BASE TABLE'", connection);
    var tableCount = Convert.ToInt32(await tables.ExecuteScalarAsync());
    if (tableCount != 70) throw new InvalidOperationException("Expected 70 rehearsal tables.");
    Console.WriteLine("MySQL rehearsal passed: 70 tables and synthetic constraint tests. Source data was not accessed.");
}
catch (MySqlException exception)
{
    Console.Error.WriteLine($"MySQL connection or setup failed (error {exception.Number}). Credentials were not printed.");
    Environment.ExitCode = 1;
}
catch (Exception exception) when (exception is InvalidOperationException or IOException)
{
    Console.Error.WriteLine(exception.Message);
    Environment.ExitCode = 1;
}
finally
{
    if (created && connection.State == System.Data.ConnectionState.Open)
    {
        await Execute("ROLLBACK");
        await Execute($"DROP DATABASE `{database}`");
    }
}

async Task Execute(string sql)
{
    await using var command = new MySqlCommand(sql, connection);
    await command.ExecuteNonQueryAsync();
}

static IEnumerable<string> ParseStatements(string file)
{
    var delimiter = ";";
    var statement = new StringBuilder();
    foreach (var line in File.ReadLines(file))
    {
        var trimmed = line.Trim();
        if (statement.Length == 0 && (trimmed.Length == 0 || trimmed.StartsWith("--"))) continue;
        if (trimmed.StartsWith("DELIMITER ", StringComparison.OrdinalIgnoreCase))
        {
            if (statement.ToString().Trim().Length != 0) throw new InvalidOperationException("Unexpected delimiter in SQL statement.");
            statement.Clear();
            delimiter = trimmed[10..].Trim();
            continue;
        }
        statement.AppendLine(line);
        if (trimmed.EndsWith(delimiter, StringComparison.Ordinal))
        {
            var sql = statement.ToString().TrimEnd();
            yield return sql[..^delimiter.Length];
            statement.Clear();
        }
    }
    if (statement.ToString().Trim().Length != 0) throw new InvalidOperationException("Unterminated SQL statement.");
}
