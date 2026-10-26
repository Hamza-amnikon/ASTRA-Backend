using System.Text.Json;
using Astra.Api.Data;
using Astra.Api.Security;
using Microsoft.Data.SqlClient;

namespace Astra.Api.Services;

public sealed record BusinessCalendar(Guid Id, TimeZoneInfo Zone, IReadOnlySet<int> WorkingDays, TimeOnly Start, TimeOnly End, IReadOnlySet<DateOnly> Holidays)
{
    public static async Task<BusinessCalendar> LoadAsync(SqlConnection connection, SqlTransaction? transaction, Guid id, CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, object?> { ["id"] = id };
        var rows = await Database.QueryAsync(connection, "SELECT id,timezone,working_days,work_start,work_end FROM dbo.sla_calendars WHERE id=@id", parameters, transaction, cancellationToken);
        if (rows.Count == 0) throw new ApiFault(400, "SLA calendar is not available.");
        var row = rows[0];
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById((string)row["timezone"]!); }
        catch (TimeZoneNotFoundException) { throw new ApiFault(400, "Use a valid IANA timezone."); }
        var holidays = (await Database.QueryAsync(connection, "SELECT holiday_date FROM dbo.sla_holidays WHERE calendar_id=@id", parameters, transaction, cancellationToken)).Select(r => DateOnly.FromDateTime((DateTime)r["holiday_date"]!)).ToHashSet();
        return new BusinessCalendar(id, zone, JsonSerializer.Deserialize<int[]>((string)row["working_days"]!)!.ToHashSet(), TimeOnly.FromTimeSpan((TimeSpan)row["work_start"]!), TimeOnly.FromTimeSpan((TimeSpan)row["work_end"]!), holidays);
    }
    private DateOnly LocalDay(DateTimeOffset instant) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, Zone).DateTime);
    private bool Working(DateOnly day) => WorkingDays.Contains(day.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)day.DayOfWeek) && !Holidays.Contains(day);
    private DateTimeOffset Instant(DateOnly day, TimeOnly clock)
    {
        var local = DateTime.SpecifyKind(day.ToDateTime(clock), DateTimeKind.Unspecified);
        // PostgreSQL resolves a DST fold/gap using the standard-side offset.
        // GetUtcOffset for an unspecified local time provides the same rule.
        return new DateTimeOffset(local, Zone.GetUtcOffset(local)).ToUniversalTime();
    }
    public DateTimeOffset AddMinutes(DateTimeOffset start, int minutes)
    {
        if (minutes <= 0) return start;
        var cursor = start; var remaining = minutes;
        for (var guard = 0; guard < 3700; guard++)
        {
            var day = LocalDay(cursor);
            if (Working(day))
            {
                var beginning = Instant(day, Start); var ending = Instant(day, End);
                if (cursor < beginning) cursor = beginning;
                if (cursor < ending)
                {
                    var available = (int)Math.Floor((ending - cursor).TotalMinutes);
                    if (remaining <= available) return cursor.AddMinutes(remaining);
                    remaining -= available;
                }
            }
            cursor = Instant(day.AddDays(1), TimeOnly.MinValue);
        }
        throw new ApiFault(400, "SLA calendar cannot satisfy target.");
    }
    public int MinutesBetween(DateTimeOffset start, DateTimeOffset end)
    {
        if (end <= start) return 0;
        var day = LocalDay(start); var final = LocalDay(end); var total = 0;
        for (var guard = 0; day <= final; guard++, day = day.AddDays(1))
        {
            if (guard >= 3700) throw new ApiFault(400, "SLA interval is too large.");
            if (!Working(day)) continue;
            var beginning = Instant(day, Start); var ending = Instant(day, End);
            var overlapStart = start > beginning ? start : beginning; var overlapEnd = end < ending ? end : ending;
            if (overlapEnd > overlapStart) total = checked(total + (int)Math.Floor((overlapEnd - overlapStart).TotalMinutes));
        }
        return total;
    }
}
