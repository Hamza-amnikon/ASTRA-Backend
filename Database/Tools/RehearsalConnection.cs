using MySqlConnector;

namespace Astra.Tools;

public static class RehearsalConnection
{
    public static MySqlConnectionStringBuilder Load(string settingsRoot)
    {
        var path = Path.Combine(settingsRoot, ".env");
        var settings = File.Exists(path)
            ? File.ReadLines(path).Where(line => !line.StartsWith('#') && line.Contains('='))
                .Select(line => line.Split('=', 2)).ToDictionary(parts => parts[0], parts => parts[1])
            : new Dictionary<string, string>();
        var connection = Environment.GetEnvironmentVariable("ASTRA_TEST_MYSQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection))
            settings.TryGetValue("ASTRA_TEST_MYSQL_CONNECTION", out connection);
        MySqlConnectionStringBuilder options;
        if (!string.IsNullOrWhiteSpace(connection))
        {
            try { options = new MySqlConnectionStringBuilder(connection); }
            catch (ArgumentException) { throw new InvalidOperationException("The private test MySQL connection is invalid; credentials were not printed."); }
        }
        else
        {
            // Compatibility with the existing isolated local preview, never a production fallback.
            if (!settings.TryGetValue("MYSQL_ROOT_PASSWORD", out var password) || string.IsNullOrWhiteSpace(password))
                throw new InvalidOperationException("Set ASTRA_TEST_MYSQL_CONNECTION for a dedicated test server before running database checks.");
            options = new MySqlConnectionStringBuilder
            {
                Server = "127.0.0.1", Port = 3307, UserID = "root", Password = password
            };
        }
        options.Database = "";
        options.AllowUserVariables = true;
        options.GuidFormat = MySqlGuidFormat.None;
        options.DateTimeKind = MySqlDateTimeKind.Utc;
        options.ConnectionTimeout = 5;
        options.DefaultCommandTimeout = 60;
        return options;
    }
}
