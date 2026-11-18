using System.Text.Json;
using MySqlConnector;

public sealed partial class MySqlStore
{
    private static Guid? ReadAssignmentUserId(object? value)
    {
        var text = value switch
        {
            JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
            string raw => raw.Trim().Trim('"'),
            _ => null
        };
        return Guid.TryParse(text, out var id) ? id : null;
    }

    private static async Task AddTicketAssignmentNames(MySqlConnection connection, string ticketId,
        List<Dictionary<string, object?>> history, ILogger<MySqlStore>? logger, CancellationToken ct)
    {
        var creation = history.FirstOrDefault(entry => Equals(entry["action"], "TICKET.CREATED"));
        Guid? initialAssignee = null;
        if (creation is not null && creation["actor_user_id"] is string creator)
        {
            try
            {
                await using var command = FCommand(connection, null, """
                    SELECT assigned_user_id FROM ticket_assignments
                    WHERE ticket_id=@ticket AND actor_user_id=@creator
                      AND reason='Initial assignment' AND created_at<=@createdAt
                    ORDER BY created_at LIMIT 1
                    """, ("ticket", ticketId), ("creator", creator), ("createdAt", creation["created_at"]));
                initialAssignee = ReadAssignmentUserId(await command.ExecuteScalarAsync(ct));
            }
            catch (MySqlException exception) when (!ct.IsCancellationRequested)
            {
                logger?.LogWarning(exception, "Could not read the initial assignment for ticket {TicketId}.", ticketId);
            }
        }

        var assignments = new List<(Dictionary<string, object?> Entry, Guid? Previous, Guid Assignee)>();
        foreach (var entry in history)
        {
            if (Equals(entry["action"], "TICKET.ASSIGNED") && ReadAssignmentUserId(entry["new_value"]) is Guid assignee)
                assignments.Add((entry, ReadAssignmentUserId(entry["previous_value"]), assignee));
        }
        if (creation is not null && initialAssignee is Guid firstAssignee)
            assignments.Add((creation, null, firstAssignee));

        var names = new Dictionary<Guid, string>();
        try
        {
            var ids = assignments.SelectMany(item => new Guid?[] { item.Previous, item.Assignee }).OfType<Guid>().Distinct();
            foreach (var batch in ids.Chunk(200))
            {
                var parameters = batch.Select((id, index) => ($"user{index}", (object?)id.ToString("D"))).ToArray();
                var placeholders = string.Join(',', parameters.Select(parameter => "@" + parameter.Item1));
                var rows = await FRows(connection, null, $"SELECT id,display_name FROM users WHERE id IN ({placeholders})", ct, parameters);
                foreach (var row in rows)
                {
                    if (ReadAssignmentUserId(row["id"]) is Guid id && row["display_name"] is string name && !string.IsNullOrWhiteSpace(name))
                        names[id] = name.Trim();
                }
            }
        }
        catch (MySqlException exception) when (!ct.IsCancellationRequested)
        {
            logger?.LogWarning(exception, "Could not resolve assignment names for ticket {TicketId}.", ticketId);
            return;
        }

        foreach (var (entry, previous, assignee) in assignments)
        {
            entry["previous_assignee_name"] = previous is Guid previousId ? names.GetValueOrDefault(previousId, "Former team member") : null;
            entry["assignee_name"] = names.GetValueOrDefault(assignee, "Former team member");
            entry["self_assignment"] = ReadAssignmentUserId(entry["actor_user_id"]) == assignee;
        }
    }
}
