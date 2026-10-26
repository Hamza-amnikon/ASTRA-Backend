namespace Astra.Api.Models;

public sealed record CalendarInput
{
    public required string Name { get; init; }
    public required string Timezone { get; init; }
    public required int[] WorkingDays { get; init; }
    public required string WorkStart { get; init; }
    public required string WorkEnd { get; init; }
    public required bool Active { get; init; }
    public required CalendarHoliday[] Holidays { get; init; }
}
