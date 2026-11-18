using MySqlConnector;

public sealed partial class MySqlStore
{
    private static async Task AddTicketActivityDetails(MySqlConnection connection, string ticketId,
        List<Dictionary<string, object?>> history, CancellationToken ct)
    {
        foreach (var (action, table, field) in new[]
        {
            ("TICKET.TAGS_CHANGED", "tags", "tag_names"),
            ("TICKET.CATEGORY_CHANGED", "categories", "category_names")
        })
        {
            var events = history.Where(entry => Equals(entry["action"], action)).ToArray();
            var ids = events.SelectMany(entry => new[] { "previous_value", "new_value" }
                .SelectMany(key => AuditIds(entry.GetValueOrDefault(key)))).Distinct().ToArray();
            var names = new Dictionary<Guid, string>();
            foreach (var batch in ids.Chunk(200))
            {
                var (parameters, placeholders) = AuditLookup(batch.Cast<object?>().ToArray());
                var rows = await FRows(connection, null,
                    $"SELECT id,name FROM {table} WHERE id IN ({placeholders})", ct, parameters);
                foreach (var row in rows)
                    if (Guid.TryParse(Convert.ToString(row["id"]), out var id))
                        names[id] = Convert.ToString(row["name"]) ?? "Unnamed record";
            }
            foreach (var entry in events)
            {
                foreach (var (key, prefix) in new[] { ("previous_value", "previous_"), ("new_value", "new_") })
                    entry[prefix + field] = AuditIds(entry.GetValueOrDefault(key))
                        .Select(id => names.GetValueOrDefault(id, $"Unavailable record ({id})")).ToArray();
            }
        }
        var helpEvents = history.Where(entry => entry["action"] is
            "TICKET.HELP_REQUESTED" or "TICKET.HELP_REQUEST_UPDATED").ToArray();
        if (helpEvents.Length > 0)
        {
            var requests = await FRows(connection, null, """
                SELECT h.id,h.expertise_needed,h.response_note,u.display_name,d.name AS department_name
                FROM help_requests h
                LEFT JOIN users u ON u.id=h.requested_user_id
                LEFT JOIN departments d ON d.id=h.department_id
                WHERE h.ticket_id=@ticket
                """, ct, ("ticket", ticketId));
            foreach (var entry in helpEvents)
            {
                var metadata = AuditObject(entry.GetValueOrDefault("new_value"));
                var id = Convert.ToString(metadata.GetValueOrDefault("helpRequestId"));
                var request = requests.FirstOrDefault(row => Convert.ToString(row["id"]) == id);
                if (request is null) continue;
                entry["help_recipient"] = request["display_name"] ?? request["department_name"];
                entry["help_expertise"] = request["expertise_needed"];
                if (Equals(entry["action"], "TICKET.HELP_REQUEST_UPDATED"))
                    entry["help_response"] = request["response_note"];
            }
        }
    }
}
