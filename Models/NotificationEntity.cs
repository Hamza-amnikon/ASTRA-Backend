using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Astra.Api.Models;

[Table("notifications", Schema = "dbo")]
public sealed class NotificationEntity
{
    [Key, Column("id")] public Guid Id { get; set; }
    [Column("user_id")] public Guid UserId { get; set; }
    [Column("ticket_id")] public Guid? TicketId { get; set; }
    [Column("change_id")] public Guid? ChangeId { get; set; }
    [Column("type")] public string Type { get; set; } = "";
    [Column("title")] public string Title { get; set; } = "";
    [Column("body")] public string Body { get; set; } = "";
    [Column("event_key"), MaxLength(450)] public string EventKey { get; set; } = "";
    [Column("read_at")] public DateTimeOffset? ReadAt { get; set; }
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; }
}
