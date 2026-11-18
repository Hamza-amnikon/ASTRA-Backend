using MySqlConnector;

public sealed record ReplayTicketAssignments(string[] TicketIds);

public static partial class FeatureApis
{
    private static void MapNotificationDelivery(RouteGroupBuilder api)
    {
        api.MapPost("/notifications/replay-ticket-assignments", async (ReplayTicketAssignments body, HttpContext c, MySqlStore s, CancellationToken ct) =>
            Results.Ok(await s.ReplayTicketAssignmentAlerts(Actor(c), body.TicketIds, ct)));
    }
}

public sealed partial class MySqlStore
{
    // In-app delivery must run independently of optional outbound email settings.
    private static async Task QueueTicketInAppEvent(MySqlConnection c, MySqlTransaction tx,
        string? actor, string ticket, string? comment, string eventName, CancellationToken ct)
    {
        var row = (await FRows(c, tx,
            "SELECT ticket_number,subject,assigned_user_id FROM tickets WHERE id=@ticket", ct,
            ("ticket", ticket))).FirstOrDefault();
        if (row is null) return;
        var created = eventName.EndsWith("ticket created", StringComparison.OrdinalIgnoreCase);
        var assignment = created || eventName is "Ticket assigned" or "Ticket claimed";
        var type = assignment ? "TICKET_ASSIGNED" : comment is not null
            ? eventName == "Internal note added" ? "INTERNAL_NOTE" : "PUBLIC_REPLY"
            : "TICKET_UPDATED";
        // Only assigned staff and existing collaborators receive a ticket event.
        // The inbox additionally rechecks the user's current ticket access.
        var recipients = await FRows(c, tx, """
            SELECT DISTINCT u.id FROM tickets t JOIN users u
              ON u.id=t.assigned_user_id OR (@assignment=0 AND EXISTS(
                SELECT 1 FROM ticket_collaborators tc WHERE tc.ticket_id=t.id AND tc.user_id=u.id))
            WHERE t.id=@ticket AND u.active=1
              AND (u.access_expires_at IS NULL OR u.access_expires_at>UTC_TIMESTAMP(6))
              AND EXISTS(SELECT 1 FROM user_roles ur WHERE ur.user_id=u.id
                AND ur.role_code IN ('ADMIN','MANAGER','TEAM_LEAD','TECHNICIAN','INTERN'))
              AND (EXISTS(SELECT 1 FROM user_roles ur WHERE ur.user_id=u.id AND ur.role_code='ADMIN')
                OR (EXISTS(SELECT 1 FROM client_users cu WHERE cu.user_id=u.id AND cu.client_id=t.client_id)
                  AND (NOT EXISTS(SELECT 1 FROM user_sites us WHERE us.user_id=u.id)
                    OR EXISTS(SELECT 1 FROM user_sites us WHERE us.user_id=u.id AND us.site_id=t.site_id))))
              AND (@created=1 OR @actor IS NULL OR u.id<>@actor)
            """, ct, ("ticket", ticket), ("assignment", assignment), ("created", created), ("actor", actor));
        var eventId = created ? "created" : comment ?? Guid.NewGuid().ToString("N");
        var actorRow = actor is null ? null : (await FRows(c, tx,
            "SELECT display_name FROM users WHERE id=@actor", ct, ("actor", actor))).FirstOrDefault();
        var actorName = actorRow?["display_name"] as string ?? "Service desk";
        var title = assignment ? $"Ticket assigned: {row["ticket_number"]}"
            : $"{actorName}: {eventName} · {row["ticket_number"]}";
        var text = row["subject"] as string ?? "Ticket update";
        if (comment is not null)
        {
            var message = (await FRows(c, tx,
                "SELECT body FROM ticket_comments WHERE id=@comment AND ticket_id=@ticket", ct,
                ("comment", comment), ("ticket", ticket))).FirstOrDefault();
            if (message?["body"] is string value) text = value.Length > 300 ? value[..300] + "…" : value;
        }
        foreach (var recipient in recipients)
        {
            var user = (string)recipient["id"]!;
            var preferences = (await FRows(c, tx,
                "SELECT ticket_assigned,public_reply FROM notification_preferences WHERE user_id=@user", ct,
                ("user", user))).FirstOrDefault();
            if (preferences is not null && ((assignment && !FBool(preferences["ticket_assigned"])) ||
                (type == "PUBLIC_REPLY" && !FBool(preferences["public_reply"])))) continue;
            // Stable creation keys make administrative replay safe to repeat.
            var key = $"ticket-event:{ticket}:{eventId}:{user}";
            if (created && (await FRows(c, tx,
                "SELECT id FROM notifications WHERE user_id=@user AND ticket_id=@ticket AND type IN ('TICKET_ASSIGNED','TICKET_TRANSFERRED') LIMIT 1",
                ct, ("user", user), ("ticket", ticket))).Count > 0) continue;
            await InsertNotification(c, tx, user, ticket, type, title, text, key, ct);
        }
    }

    public async Task<object> ReplayTicketAssignmentAlerts(string actor, string[]? ticketIds, CancellationToken ct)
    {
        await RequireAdmin(actor, ct);
        if (ticketIds is null || ticketIds.Length is < 1 or > 20 || ticketIds.Any(id => !Guid.TryParse(id, out _)))
            throw new ArgumentException("Choose between 1 and 20 valid ticket IDs.");
        var ids = ticketIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        await using var c = CreateConnection();
        await c.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        foreach (var id in ids)
        {
            if ((await FRows(c, tx, "SELECT id FROM tickets WHERE id=@id FOR UPDATE", ct, ("id", id))).Count == 0)
                throw new ArgumentException("A selected ticket does not exist.");
            await QueueTicketInAppEvent(c, tx, actor, id, null, "Ticket created", ct);
        }
        await AddAudit(c, tx, actor, "NOTIFICATIONS.ASSIGNMENTS_REPLAYED", "USER", actor, new { ticketIds = ids }, ct);
        await tx.CommitAsync(ct);
        return new { processed = ids.Length };
    }
}
