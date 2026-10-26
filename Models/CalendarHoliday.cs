namespace Astra.Api.Models;

public sealed record CalendarHoliday
{
    public required DateOnly Date { get; init; }
    public required string Name { get; init; }
}
