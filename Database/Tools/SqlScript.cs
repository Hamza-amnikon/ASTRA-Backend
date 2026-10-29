using System.Text;

namespace Astra.Tools;

public static class SqlScript
{
    public static IEnumerable<string> Statements(string file)
    {
        var delimiter = ";";
        var statement = new StringBuilder();
        foreach (var line in File.ReadLines(file))
        {
            var trimmed = line.Trim();
            if (statement.Length == 0 && (trimmed.Length == 0 || trimmed.StartsWith("--"))) continue;
            if (trimmed.StartsWith("DELIMITER ", StringComparison.OrdinalIgnoreCase))
            {
                if (statement.ToString().Trim().Length != 0) throw new InvalidOperationException("Unexpected SQL delimiter.");
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
}
