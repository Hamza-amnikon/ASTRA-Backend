using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Astra.Api.Models;

[Table("notification_preferences", Schema = "dbo")]
public sealed class NotificationPreferenceEntity
{
    [Key, Column("user_id")] public Guid UserId { get; set; }
    [Column("ticket_assigned")] public bool TicketAssigned { get; set; } = true;
    [Column("ticket_transferred")] public bool TicketTransferred { get; set; } = true;
    [Column("public_reply")] public bool PublicReply { get; set; } = true;
    [Column("sla_at_risk")] public bool SlaAtRisk { get; set; } = true;
    [Column("sla_breached")] public bool SlaBreached { get; set; } = true;
    [Column("ticket_unattended")] public bool TicketUnattended { get; set; } = true;
    [Column("updated_at")] public DateTimeOffset UpdatedAt { get; set; }
}
