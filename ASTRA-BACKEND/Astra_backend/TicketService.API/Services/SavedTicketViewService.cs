using System.Globalization;
using System.Text.Json.Nodes;
using Astra.Shared.Data;
using Astra.Shared.Security;

namespace TicketService.API.Services;

public sealed class SavedTicketViewService(Database database)
{
    private static readonly HashSet<string> FilterNames = new(StringComparer.Ordinal)
    {
        "q", "dashboardFilter", "status", "priority", "clientId", "siteId",
        "assignedUserId", "categoryId", "tagId", "source", "slaStatus", "from", "to"
    };
    private static readonly HashSet<string> IdentifierFilters = new(StringComparer.Ordinal)
    {
        "clientId", "siteId", "assignedUserId", "categoryId", "tagId"
    };
    private static readonly string[] DashboardFilters =
        ["WAITING", "RESOLVED_RECENTLY", "PENDING_APPROVAL", "AWAITING_FIRST_RESPONSE"];

    private static JsonObject Validate(SaveTicketViewInput input)
    {
        if (input.Name is null || input.Name.Trim().Length is < 2 or > 80 || input.Query is null)
            throw new ApiFault(400, "Enter a view name of 2 to 80 characters and valid ticket filters.");
        var stored = new JsonObject();
        foreach (var (key, node) in input.Query)
        {
            if (!FilterNames.Contains(key)) throw new ApiFault(400, "Unknown saved-view filter.");
            if (node is null) continue;
            if (node is not JsonValue value || !value.TryGetValue<string>(out var text))
                throw new ApiFault(400, "Saved-view filters must be text values.");
            if (text.Length == 0) continue;
            var valid = key switch
            {
                "q" => text.Length <= 500,
                "source" => text.Length <= 50,
                "status" => TicketReadService.Statuses.Contains(text),
                "priority" => TicketReadService.Priorities.Contains(text),
                "slaStatus" => TicketReadService.SlaStates.Contains(text),
                "dashboardFilter" => DashboardFilters.Contains(text),
                "from" or "to" => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out _),
                _ => IdentifierFilters.Contains(key) && Guid.TryParse(text, out _)
            };
            if (!valid) throw new ApiFault(400, "Invalid saved-view filter: " + key);
            stored[key] = text;
        }
        if (stored["from"] is not null && stored["to"] is not null &&
            DateTimeOffset.Parse(stored["from"]!.GetValue<string>(), CultureInfo.InvariantCulture) >
            DateTimeOffset.Parse(stored["to"]!.GetValue<string>(), CultureInfo.InvariantCulture))
            throw new ApiFault(400, "From date must be before To date.");
        return stored;
    }

    public async Task<object> ListAsync(Actor actor, CancellationToken cancellationToken)
    {
        TicketReadService.RequireStaff(actor);
        await using var connection = await database.OpenAsync(cancellationToken);
        var rows = await Database.QueryAsync(connection,
            "SELECT id,name,`query`,created_at,updated_at FROM saved_ticket_views WHERE user_id=@user ORDER BY LOWER(name),id",
            new Dictionary<string, object?> { ["user"] = actor.Id }, cancellationToken: cancellationToken);
        foreach (var row in rows) row["query"] = JsonNode.Parse((string)row["query"]!);
        return rows;
    }

    public async Task<object> SaveAsync(Actor actor, SaveTicketViewInput input, CancellationToken cancellationToken)
    {
        TicketReadService.RequireStaff(actor);
        var filters = Validate(input);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var parameters = new Dictionary<string, object?>
        {
            ["id"] = Guid.NewGuid(), ["user"] = actor.Id,
            ["name"] = input.Name.Trim(), ["filters"] = filters.ToJsonString()
        };
        await Database.ExecuteAsync(connection, """
            INSERT INTO saved_ticket_views(id,user_id,name,`query`) VALUES(@id,@user,@name,@filters)
            ON DUPLICATE KEY UPDATE `query`=@filters,updated_at=UTC_TIMESTAMP(6)
            """, parameters, transaction, cancellationToken);
        var row = (await Database.QueryAsync(connection,
            "SELECT id,name,`query` FROM saved_ticket_views WHERE user_id=@user AND name=@name",
            parameters, transaction, cancellationToken)).Single();
        row["query"] = JsonNode.Parse((string)row["query"]!);
        await transaction.CommitAsync(cancellationToken);
        return row;
    }

    public async Task<object> DeleteAsync(Actor actor, Guid id, CancellationToken cancellationToken)
    {
        TicketReadService.RequireStaff(actor);
        await using var connection = await database.OpenAsync(cancellationToken);
        var deleted = await Database.ExecuteAsync(connection,
            "DELETE FROM saved_ticket_views WHERE id=@id AND user_id=@user",
            new Dictionary<string, object?> { ["id"] = id, ["user"] = actor.Id }, cancellationToken: cancellationToken);
        if (deleted == 0) throw new ApiFault(404, "Saved view not found.");
        return new { id, deleted = true };
    }
}
