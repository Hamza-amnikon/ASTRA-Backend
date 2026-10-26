namespace Astra.Api.Models;

public sealed record SlaPolicyInput
{
    public required int ResponseMinutes { get; init; }
    public required int ResolutionMinutes { get; init; }
    public required int WarningPercent { get; init; }
    public required bool Active { get; init; }
    public required Guid CalendarId { get; init; }
    public bool ApplyToOpenTickets { get; init; }
}
