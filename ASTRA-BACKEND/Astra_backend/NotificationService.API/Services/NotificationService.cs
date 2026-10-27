using System.Text.Json;
using Astra.Shared.Data;
using Astra.Shared.Security;
using MySqlConnector;

namespace NotificationService.API.Services;

public sealed class NotificationService(Database database)
{
    public async Task<object> ListAsync(Actor actor, string? rawLimit, CancellationToken cancellationToken)
    {
        var limit = double.TryParse(rawLimit, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed) && parsed != 0 ? (int)Math.Clamp(parsed, 1, 50) : 20;
        var parameters = ScopeSql.Parameters(actor); parameters["user"] = actor.Id; parameters["limit"] = limit; parameters["wide"] = actor.Roles.Overlaps(["TEAM_LEAD", "MANAGER", "ADMIN"]); parameters["team"] = actor.Permissions.Contains("TICKETS_TEAM_VIEW"); parameters["departments"] = JsonSerializer.Serialize(actor.DepartmentIds ?? []);
        var access = """
            n.user_id=@user AND(n.ticket_id IS NULL OR @scopeAdmin=1 OR
             (t.client_id IN(SELECT `value` FROM JSON_TABLE(@scopeClients, '$[*]' COLUMNS(`value` VARCHAR(100) PATH '$')) AS json_values) AND
              (@wide=1 OR t.assigned_user_id=@user OR EXISTS(SELECT 1 FROM ticket_collaborators tc WHERE tc.ticket_id=t.id AND tc.user_id=@user) OR
               EXISTS(SELECT 1 FROM help_requests hr WHERE hr.ticket_id=t.id AND hr.status='PENDING' AND(hr.requested_user_id=@user OR hr.department_id IN(SELECT `value` FROM JSON_TABLE(@departments, '$[*]' COLUMNS(`value` VARCHAR(100) PATH '$')) AS json_values))) OR
               (@team=1 AND(@scopeSiteCount=0 OR t.site_id IN(SELECT `value` FROM JSON_TABLE(@scopeSites, '$[*]' COLUMNS(`value` VARCHAR(100) PATH '$')) AS json_values))))))
            """;
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (MySqlTransaction)await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        var items = await Database.QueryAsync(connection, $"SELECT n.id,n.ticket_id,n.change_id,n.type,n.title,n.body,n.read_at,n.created_at,t.ticket_number FROM notifications n LEFT JOIN tickets t ON t.id=n.ticket_id WHERE {access} ORDER BY n.created_at DESC LIMIT @limit", parameters, transaction, cancellationToken);
        var unread = (await Database.QueryAsync(connection, $"SELECT COUNT(*) AS n FROM notifications n LEFT JOIN tickets t ON t.id=n.ticket_id WHERE {access} AND n.read_at IS NULL", parameters, transaction, cancellationToken)).Single()["n"];
        await transaction.CommitAsync(cancellationToken); return new { unread, items };
    }
    public async Task<object> ReadPreferencesAsync(Guid user, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        var rows = await Database.QueryAsync(connection, "SELECT * FROM notification_preferences WHERE user_id=@user",
            new Dictionary<string, object?> { ["user"] = user }, cancellationToken: cancellationToken);
        if (rows.Count == 0) return new NotificationPreferences
        {
            TicketAssigned = true, TicketTransferred = true, PublicReply = true,
            SlaAtRisk = true, SlaBreached = true, TicketUnattended = true
        };
        var row = rows[0];
        return new NotificationPreferences
        {
            TicketAssigned = Convert.ToBoolean(row["ticket_assigned"]),
            TicketTransferred = Convert.ToBoolean(row["ticket_transferred"]),
            PublicReply = Convert.ToBoolean(row["public_reply"]),
            SlaAtRisk = Convert.ToBoolean(row["sla_at_risk"]),
            SlaBreached = Convert.ToBoolean(row["sla_breached"]),
            TicketUnattended = Convert.ToBoolean(row["ticket_unattended"])
        };
    }

    public async Task<object> SavePreferencesAsync(Guid user, NotificationPreferences input, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await Database.ExecuteAsync(connection, """
            INSERT INTO notification_preferences(user_id,ticket_assigned,ticket_transferred,public_reply,sla_at_risk,sla_breached,ticket_unattended)
            VALUES(@user,@assigned,@transferred,@reply,@risk,@breached,@unattended)
            ON DUPLICATE KEY UPDATE ticket_assigned=@assigned,ticket_transferred=@transferred,public_reply=@reply,
                sla_at_risk=@risk,sla_breached=@breached,ticket_unattended=@unattended,updated_at=UTC_TIMESTAMP(6)
            """, new Dictionary<string, object?>
            {
                ["user"] = user, ["assigned"] = input.TicketAssigned, ["transferred"] = input.TicketTransferred,
                ["reply"] = input.PublicReply, ["risk"] = input.SlaAtRisk,
                ["breached"] = input.SlaBreached, ["unattended"] = input.TicketUnattended
            }, cancellationToken: cancellationToken);
        return input;
    }

    public async Task MarkAsync(Guid user, Guid? id, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await Database.ExecuteAsync(connection,
            "UPDATE notifications SET read_at=UTC_TIMESTAMP(6) WHERE user_id=@user AND read_at IS NULL AND (@id IS NULL OR id=@id)",
            new Dictionary<string, object?> { ["user"] = user, ["id"] = id }, cancellationToken: cancellationToken);
    }
}
