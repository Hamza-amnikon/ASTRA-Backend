using System.Text.Json;
using Astra.Api.Data;
using Astra.Api.Security;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Services;

public sealed class NotificationService(Database database, AstraDbContext context)
{
    public static readonly IReadOnlyDictionary<string, string> Preferences = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["TICKET_ASSIGNED"] = "ticket_assigned",
        ["TICKET_TRANSFERRED"] = "ticket_transferred",
        ["PUBLIC_REPLY"] = "public_reply",
        ["SLA_AT_RISK"] = "sla_at_risk",
        ["SLA_BREACHED"] = "sla_breached",
        ["TICKET_UNATTENDED"] = "ticket_unattended"
    };
    public static Task EnqueueAsync(SqlConnection connection, SqlTransaction transaction, Guid user, Guid? ticket, string type, string title, string body, string eventKey, CancellationToken cancellationToken = default)
    {
        if (!Preferences.TryGetValue(type, out var column)) throw new ArgumentException("Invalid notification type.", nameof(type));
        var parameters = new Dictionary<string, object?> { ["user"] = user, ["ticket"] = ticket, ["type"] = type, ["title"] = title, ["body"] = body, ["key"] = eventKey };
        return Database.ExecuteAsync(connection, $"INSERT dbo.notifications(user_id,ticket_id,type,title,body,event_key) SELECT @user,@ticket,@type,@title,@body,@key WHERE COALESCE((SELECT {column} FROM dbo.notification_preferences WHERE user_id=@user),1)=1 AND NOT EXISTS(SELECT 1 FROM dbo.notifications WITH(UPDLOCK,HOLDLOCK) WHERE event_key=@key)", parameters, transaction, cancellationToken);
    }
    public async Task<object> ListAsync(Actor actor, string? rawLimit, CancellationToken cancellationToken)
    {
        var limit = double.TryParse(rawLimit, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed) && parsed != 0 ? (int)Math.Clamp(parsed, 1, 50) : 20;
        var parameters = ScopeSql.Parameters(actor); parameters["user"] = actor.Id; parameters["limit"] = limit; parameters["wide"] = actor.Roles.Overlaps(["TEAM_LEAD", "MANAGER", "ADMIN"]); parameters["team"] = actor.Permissions.Contains("TICKETS_TEAM_VIEW"); parameters["departments"] = JsonSerializer.Serialize(actor.DepartmentIds ?? []);
        var access = """
            n.user_id=@user AND(n.ticket_id IS NULL OR @scopeAdmin=1 OR
             (t.client_id IN(SELECT CONVERT(uniqueidentifier,[value]) FROM OPENJSON(@scopeClients)) AND
              (@wide=1 OR t.assigned_user_id=@user OR EXISTS(SELECT 1 FROM dbo.ticket_collaborators tc WHERE tc.ticket_id=t.id AND tc.user_id=@user) OR
               EXISTS(SELECT 1 FROM dbo.help_requests hr WHERE hr.ticket_id=t.id AND hr.status='PENDING' AND(hr.requested_user_id=@user OR hr.department_id IN(SELECT CONVERT(uniqueidentifier,[value]) FROM OPENJSON(@departments)))) OR
               (@team=1 AND(@scopeSiteCount=0 OR t.site_id IN(SELECT CONVERT(uniqueidentifier,[value]) FROM OPENJSON(@scopeSites)))))))
            """;
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        var items = await Database.QueryAsync(connection, $"SELECT TOP(@limit) n.id,n.ticket_id,n.change_id,n.type,n.title,n.body,n.read_at,n.created_at,t.ticket_number FROM dbo.notifications n LEFT JOIN dbo.tickets t ON t.id=n.ticket_id WHERE {access} ORDER BY n.created_at DESC", parameters, transaction, cancellationToken);
        var unread = (await Database.QueryAsync(connection, $"SELECT COUNT(*) AS n FROM dbo.notifications n LEFT JOIN dbo.tickets t ON t.id=n.ticket_id WHERE {access} AND n.read_at IS NULL", parameters, transaction, cancellationToken)).Single()["n"];
        await transaction.CommitAsync(cancellationToken); return new { unread, items };
    }
    public async Task<object> ReadPreferencesAsync(Guid user, CancellationToken cancellationToken)
    {
        var preferences = await context.NotificationPreferences.AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == user, cancellationToken);
        return preferences is null
            ? new NotificationPreferences { TicketAssigned = true, TicketTransferred = true, PublicReply = true,
                SlaAtRisk = true, SlaBreached = true, TicketUnattended = true }
            : new NotificationPreferences { TicketAssigned = preferences.TicketAssigned,
                TicketTransferred = preferences.TicketTransferred, PublicReply = preferences.PublicReply,
                SlaAtRisk = preferences.SlaAtRisk, SlaBreached = preferences.SlaBreached,
                TicketUnattended = preferences.TicketUnattended };
    }

    public async Task<object> SavePreferencesAsync(Guid user, NotificationPreferences input, CancellationToken cancellationToken)
    {
        // Serialize concurrent first writes for the same account. Keep the
        // existing row/range lock semantics while EF tracks the entity update.
        await using var transaction = await context.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, cancellationToken);
        var preferences = await context.NotificationPreferences.FromSqlInterpolated(
            $"SELECT * FROM dbo.notification_preferences WITH(UPDLOCK,HOLDLOCK) WHERE user_id={user}")
            .SingleOrDefaultAsync(cancellationToken);
        if (preferences is null)
        {
            preferences = new NotificationPreferenceEntity { UserId = user };
            context.NotificationPreferences.Add(preferences);
        }
        preferences.TicketAssigned = input.TicketAssigned;
        preferences.TicketTransferred = input.TicketTransferred;
        preferences.PublicReply = input.PublicReply;
        preferences.SlaAtRisk = input.SlaAtRisk;
        preferences.SlaBreached = input.SlaBreached;
        preferences.TicketUnattended = input.TicketUnattended;
        preferences.UpdatedAt = DateTimeOffset.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return input;
    }

    public async Task MarkAsync(Guid user, Guid? id, CancellationToken cancellationToken)
    {
        var query = context.Notifications.Where(item => item.UserId == user && item.ReadAt == null);
        if (id.HasValue) query = query.Where(item => item.Id == id.Value);
        await query.ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ReadAt,
            (DateTimeOffset?)DateTimeOffset.UtcNow), cancellationToken);
    }
}
