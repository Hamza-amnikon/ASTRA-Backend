using MySqlConnector;
namespace Astra.Shared.Services;

public static class NotificationOutbox
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
    public static Task EnqueueAsync(MySqlConnection connection, MySqlTransaction transaction, Guid user, Guid? ticket, string type, string title, string body, string eventKey, CancellationToken cancellationToken = default)
    {
        if (!Preferences.TryGetValue(type, out var column)) throw new ArgumentException("Invalid notification type.", nameof(type));
        var parameters = new Dictionary<string, object?> { ["user"] = user, ["ticket"] = ticket, ["type"] = type, ["title"] = title, ["body"] = body, ["key"] = eventKey };
        return Database.ExecuteAsync(connection, $"INSERT INTO notifications(user_id,ticket_id,type,title,body,event_key) SELECT @user,@ticket,@type,@title,@body,@key WHERE COALESCE((SELECT {column} FROM notification_preferences WHERE user_id=@user),1)=1 AND NOT EXISTS(SELECT 1 FROM notifications WHERE event_key=@key) FOR UPDATE", parameters, transaction, cancellationToken);
    }
}
