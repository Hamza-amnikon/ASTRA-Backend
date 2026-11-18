using System.Text.Json;
using MySqlConnector;

public sealed partial class MySqlStore
{
    private async Task<List<Dictionary<string, object?>>> ReadAuditHistory(CancellationToken ct, string? teamLeadId = null)
    {
        if (ConnectionString is null) return [];
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        var scope = teamLeadId is null ? "" : $"""
            WHERE (a.entity_type='TICKET' AND EXISTS(SELECT 1 FROM tickets t WHERE t.id=a.entity_id AND {FScope("t")}))
               OR (a.entity_type='CHANGE' AND EXISTS(SELECT 1 FROM changes c WHERE c.id=a.entity_id AND {FScope("c")}))
               OR (a.entity_type='PROBLEM' AND EXISTS(SELECT 1 FROM problems p WHERE p.id=a.entity_id AND {FScope("p")}))
            """;
        var rows = await FRows(connection, null, $"""
            SELECT a.id,a.actor_user_id,a.action,a.entity_type,a.entity_id,a.details,a.created_at,
                   actor.display_name AS actor_name,
                   COALESCE(t.ticket_number,ch.change_number,p.problem_number,
                            tag.name,cat.name,cl.name,s.name,contact.name,person.display_name) AS entity_name,
                   t.subject AS ticket_subject
            FROM audit_logs a
            LEFT JOIN users actor ON actor.id=a.actor_user_id
            LEFT JOIN tickets t ON a.entity_type='TICKET' AND t.id=a.entity_id
            LEFT JOIN changes ch ON a.entity_type='CHANGE' AND ch.id=a.entity_id
            LEFT JOIN problems p ON a.entity_type='PROBLEM' AND p.id=a.entity_id
            LEFT JOIN tags tag ON a.entity_type IN ('TAG','TAGS') AND tag.id=a.entity_id
            LEFT JOIN categories cat ON a.entity_type IN ('CATEGORY','CATEGORIES') AND cat.id=a.entity_id
            LEFT JOIN clients cl ON a.entity_type IN ('CLIENT','CLIENTS') AND cl.id=a.entity_id
            LEFT JOIN sites s ON a.entity_type IN ('SITE','SITES') AND s.id=a.entity_id
            LEFT JOIN contacts contact ON a.entity_type IN ('CONTACT','CONTACTS') AND contact.id=a.entity_id
            LEFT JOIN users person ON a.entity_type='USER' AND person.id=a.entity_id
            {scope}
            ORDER BY a.created_at DESC,a.id DESC LIMIT 500
            """, ct, ("actor", teamLeadId), ("admin", false));

        var entries = rows.Select(row => (Row: row, Details: AuditObject(row["details"]))).ToArray();
        var legacy = entries.Where(entry =>
            (entry.Row["action"] is "TICKET.UPDATED" or "TICKET.ASSIGNED") &&
            !entry.Details.ContainsKey("after")).ToArray();
        var history = new List<Dictionary<string, object?>>();
        foreach (var batch in legacy.Chunk(200))
        {
            var ids = batch.Select(entry => entry.Row["id"]).ToArray();
            var (parameters, placeholders) = AuditLookup(ids);
            history.AddRange(await FRows(connection, null, $"""
                SELECT a.id AS audit_id,h.action,h.previous_value,h.new_value
                FROM audit_logs a
                JOIN ticket_history h ON h.ticket_id=a.entity_id AND h.actor_user_id=a.actor_user_id
                WHERE a.id IN ({placeholders})
                  AND h.created_at BETWEEN a.created_at - INTERVAL 5 SECOND AND a.created_at
                  AND ((a.action='TICKET.ASSIGNED' AND h.action='TICKET.ASSIGNED')
                    OR (a.action='TICKET.UPDATED' AND h.action IN ('TICKET.STATUS_CHANGED','TICKET.PRIORITY_CHANGED','TICKET.CATEGORY_CHANGED','TICKET.TAGS_CHANGED')))
                  AND NOT EXISTS (
                    SELECT 1 FROM audit_logs prior
                    WHERE prior.entity_type=a.entity_type AND prior.entity_id=a.entity_id AND prior.action=a.action
                      AND prior.id<>a.id AND prior.created_at BETWEEN h.created_at AND a.created_at
                  )
                """, ct, parameters));
        }
        var historyByEvent = history.ToLookup(row => row["audit_id"]);
        foreach (var (row, details) in legacy)
            AddLegacyAuditChanges(details, historyByEvent[row["id"]]);

        var references = new Dictionary<(string Key, Guid Id), string>();
        foreach (var (keys, table, name) in new[]
        {
            (new[] { "assignedUserId", "supervisorUserId", "ownerUserId" }, "users", "display_name"),
            (new[] { "categoryId", "category_id", "parent_id" }, "categories", "name"),
            (new[] { "tagIds" }, "tags", "name"),
            (new[] { "clientId", "client_id", "clientIds" }, "clients", "name"),
            (new[] { "siteId", "site_id", "siteIds" }, "sites", "name")
        })
        {
            var ids = entries
                .SelectMany(entry => new[] { entry.Details, AuditObject(entry.Details.GetValueOrDefault("before")), AuditObject(entry.Details.GetValueOrDefault("after")) })
                .SelectMany(details => keys.SelectMany(key => AuditIds(details.GetValueOrDefault(key)))).Distinct();
            foreach (var batch in ids.Chunk(200))
            {
                var (parameters, placeholders) = AuditLookup(batch.Cast<object?>().ToArray());
                var matches = await FRows(connection, null, $"SELECT id,{name} AS label FROM {table} WHERE id IN ({placeholders})", ct, parameters);
                foreach (var match in matches)
                    if (Guid.TryParse(Convert.ToString(match["id"]), out var id) && match["label"] is string label)
                        foreach (var key in keys) references[(key, id)] = label;
            }
        }
        foreach (var (row, details) in entries)
        {
            ResolveAuditReferences(details, references);
            foreach (var key in new[] { "before", "after" })
            {
                if (!details.TryGetValue(key, out var snapshot)) continue;
                var fields = AuditObject(snapshot);
                ResolveAuditReferences(fields, references);
                details[key] = fields;
            }
            if (details.Count > 0) row["details"] = details;
        }
        return rows;
    }

    private static ((string Name, object? Value)[] Parameters, string Placeholders) AuditLookup(object?[] ids)
    {
        var parameters = ids.Select((id, index) => ($"key{index}", id is Guid guid ? (object?)guid.ToString("D") : id)).ToArray();
        return (parameters, string.Join(',', parameters.Select(parameter => "@" + parameter.Item1)));
    }

    private static Dictionary<string, object?> AuditObject(object? value)
    {
        if (value is Dictionary<string, object?> fields) return fields;
        if (value is string json)
        {
            try { using var document = JsonDocument.Parse(json); value = document.RootElement.Clone(); }
            catch (JsonException) { return []; }
        }
        return value is JsonElement { ValueKind: JsonValueKind.Object } element
            ? element.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value)
            : [];
    }

    private static IEnumerable<Guid> AuditIds(object? value)
    {
        if (value is JsonElement { ValueKind: JsonValueKind.Array } array)
            return array.EnumerateArray().SelectMany(item => AuditIds(item));
        return Guid.TryParse(Convert.ToString(value), out var id) ? [id] : [];
    }

    private static void ResolveAuditReferences(Dictionary<string, object?> fields, Dictionary<(string Key, Guid Id), string> names)
    {
        foreach (var key in fields.Keys.ToArray())
        {
            var value = fields[key];
            var labels = AuditIds(value).Select(id => names.GetValueOrDefault((key, id), "Unavailable record")).ToArray();
            if (labels.Length == 0 || !labels.Any(label => label != "Unavailable record")) continue;
            fields[key] = value is JsonElement { ValueKind: JsonValueKind.Array } ? labels : labels[0];
        }
    }

    private static void AddLegacyAuditChanges(Dictionary<string, object?> details, IEnumerable<Dictionary<string, object?>> history)
    {
        var before = new Dictionary<string, object?>();
        var after = new Dictionary<string, object?>();
        foreach (var (action, field) in new[]
        {
            ("TICKET.ASSIGNED", "assignedUserId"), ("TICKET.STATUS_CHANGED", "status"),
            ("TICKET.PRIORITY_CHANGED", "priority"), ("TICKET.CATEGORY_CHANGED", "categoryId"),
            ("TICKET.TAGS_CHANGED", "tagIds")
        })
        {
            if (!details.TryGetValue(field, out var recorded) || recorded is not JsonElement expected ||
                expected.ValueKind == JsonValueKind.Null) continue;
            var candidates = history.Where(row => Equals(row["action"], action) &&
                row["new_value"] is JsonElement actual &&
                (field == "tagIds" ? AuditIds(expected).ToHashSet().SetEquals(AuditIds(actual)) : expected.ToString() == actual.ToString()))
                .Take(2).ToArray();
            if (candidates.Length != 1) continue;
            before[field] = candidates[0]["previous_value"];
            after[field] = recorded;
            details.Remove(field);
        }
        if (after.Count == 0) return;
        details["before"] = before;
        details["after"] = after;
    }
}
