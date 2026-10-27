namespace NotificationService.API.DTOs;

public sealed record NotificationPreferences
{
    public required bool TicketAssigned { get; init; }
    public required bool TicketTransferred { get; init; }
    public required bool PublicReply { get; init; }
    public required bool SlaAtRisk { get; init; }
    public required bool SlaBreached { get; init; }
    public required bool TicketUnattended { get; init; }
}
