using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Astra.Shared.Data;
using Astra.Shared.Security;
using MySqlConnector;

namespace SlaService.API.Services;

public sealed class SlaConfigurationService(Database database)
{
    public static string Validate(CalendarInput input)
    {
        if (input.Name is null || input.Name.Length > 120 || input.Name.Trim().Length < 2 || input.Timezone is null || input.Timezone.Length > 80 || input.WorkingDays is null || input.WorkingDays.Length is < 1 or > 7 || input.WorkingDays.Distinct().Count() != input.WorkingDays.Length || input.WorkingDays.Any(d => d is < 1 or > 7) || input.Holidays is null || input.Holidays.Length > 100 || input.Holidays.Any(h => h.Name is null || h.Name.Length is < 1 or > 120) || !Regex.IsMatch(input.WorkStart ?? "", "^([01][0-9]|2[0-3]):[0-5][0-9]$", RegexOptions.CultureInvariant) || !Regex.IsMatch(input.WorkEnd ?? "", "^([01][0-9]|2[0-3]):[0-5][0-9]$", RegexOptions.CultureInvariant) || string.CompareOrdinal(input.WorkStart, input.WorkEnd) >= 0) throw new ApiFault(400, "Invalid SLA calendar details.");
        try { TimeZoneInfo.FindSystemTimeZoneById(input.Timezone); } catch (TimeZoneNotFoundException) { throw new ApiFault(400, "Use a valid IANA timezone."); }
        return input.Timezone;
    }
    public async Task<object> PoliciesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        return await Database.QueryAsync(connection, "SELECT p.id,p.priority,p.response_minutes,p.resolution_minutes,p.warning_percent,p.active,p.calendar_id,c.name AS calendar_name,p.updated_at FROM sla_policies p JOIN sla_calendars c ON c.id=p.calendar_id ORDER BY CASE p.priority WHEN 'P1_CRITICAL' THEN 1 WHEN 'P2_HIGH' THEN 2 WHEN 'P3_MEDIUM' THEN 3 ELSE 4 END", cancellationToken: cancellationToken);
    }
    public async Task<object> CalendarsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        var rows = await Database.QueryAsync(connection, "SELECT id,name,timezone,working_days,work_start,work_end,active FROM sla_calendars ORDER BY name", cancellationToken: cancellationToken);
        var holidays = await Database.QueryAsync(connection, "SELECT id,calendar_id,holiday_date AS date,name FROM sla_holidays ORDER BY holiday_date", cancellationToken: cancellationToken);
        foreach (var row in rows)
        {
            row["working_days"] = JsonSerializer.Deserialize<int[]>((string)row["working_days"]!);
            row["work_start"] = ((TimeSpan)row["work_start"]!).ToString("hh\\:mm\\:ss", CultureInfo.InvariantCulture); row["work_end"] = ((TimeSpan)row["work_end"]!).ToString("hh\\:mm\\:ss", CultureInfo.InvariantCulture);
            row["holidays"] = holidays.Where(h => (Guid)h["calendar_id"]! == (Guid)row["id"]!).Select(h => new { id = h["id"], date = DateOnly.FromDateTime((DateTime)h["date"]!).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), name = h["name"] }).ToArray();
        }
        return rows;
    }
    public async Task<object> SaveCalendarAsync(Actor actor, CalendarInput input, Guid? id, CancellationToken cancellationToken)
    {
        var zone = Validate(input);
        await using var connection = await database.OpenAsync(cancellationToken); await using var transaction = (MySqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var columns = string.Join(',', ColumnCatalog.Columns("sla_calendars").Select(c => "`" + c + "`"));
        var parameters = new Dictionary<string, object?> { ["id"] = id, ["name"] = input.Name.Trim(), ["zone"] = input.Timezone, ["sqlZone"] = zone, ["days"] = JsonSerializer.Serialize(input.WorkingDays), ["start"] = TimeOnly.ParseExact(input.WorkStart, "HH:mm", CultureInfo.InvariantCulture).ToTimeSpan(), ["end"] = TimeOnly.ParseExact(input.WorkEnd, "HH:mm", CultureInfo.InvariantCulture).ToTimeSpan(), ["active"] = input.Active, ["actor"] = actor.Id };
        var prior = id.HasValue ? await Database.QueryAsync(connection, "SELECT " + columns + " FROM sla_calendars WHERE id=@id", parameters, transaction, cancellationToken) : [];
        if (id.HasValue && prior.Count == 0) throw new ApiFault(400, "Calendar not found.");
        if (id.HasValue && !input.Active && (await Database.QueryAsync(connection, "SELECT id FROM sla_policies WHERE calendar_id=@id AND active=1 UNION ALL SELECT s.ticket_id FROM ticket_sla s JOIN tickets t ON t.id=s.ticket_id WHERE s.calendar_id=@id AND t.status NOT IN('RESOLVED','CLOSED','CANCELLED') LIMIT 1", parameters, transaction, cancellationToken)).Count > 0) throw new ApiFault(400, "An active SLA policy is using this calendar.");
        if ((await Database.QueryAsync(connection, "SELECT @sqlZone AS name WHERE CONVERT_TZ(UTC_TIMESTAMP(6),'+00:00',@sqlZone) IS NOT NULL", parameters, transaction, cancellationToken)).Count == 0) throw new ApiFault(400, "This calendar timezone is not available on the database server.");
        parameters["id"] ??= Guid.NewGuid();
        await Database.ExecuteAsync(connection, id.HasValue ? "UPDATE sla_calendars SET name=@name,timezone=@zone,sql_timezone=@sqlZone,working_days=@days,work_start=@start,work_end=@end,active=@active,updated_by_user_id=@actor,updated_at=UTC_TIMESTAMP(6) WHERE id=@id" : "INSERT INTO sla_calendars(id,name,timezone,sql_timezone,working_days,work_start,work_end,active,updated_by_user_id) VALUES(@id,@name,@zone,@sqlZone,@days,@start,@end,@active,@actor)", parameters, transaction, cancellationToken);
        await Database.ExecuteAsync(connection, "DELETE FROM sla_holidays WHERE calendar_id=@id", parameters, transaction, cancellationToken);
        foreach (var holiday in input.Holidays)
            await Database.ExecuteAsync(connection, "INSERT INTO sla_holidays(calendar_id,holiday_date,name) VALUES(@id,@date,@name)", new Dictionary<string, object?> { ["id"] = parameters["id"], ["date"] = holiday.Date.ToDateTime(TimeOnly.MinValue), ["name"] = holiday.Name.Trim() }, transaction, cancellationToken);
        await Audit.WriteAsync(connection, transaction, actor.Id, id.HasValue ? "SLA_CALENDAR.UPDATED" : "SLA_CALENDAR.CREATED", "SLA_CALENDAR", (Guid)parameters["id"]!, id.HasValue ? new { before = prior[0], after = input } : (object)new { name = input.Name }, cancellationToken);
        await transaction.CommitAsync(cancellationToken); return new { id = parameters["id"] };
    }
    public static void ValidatePolicy(string priority, SlaPolicyInput input)
    {
        if (priority is not ("P1_CRITICAL" or "P2_HIGH" or "P3_MEDIUM" or "P4_LOW") || input.ResponseMinutes is < 1 or > 525600 || input.ResolutionMinutes is < 1 or > 525600 || input.WarningPercent is < 1 or > 99 || input.ResolutionMinutes < input.ResponseMinutes) throw new ApiFault(400, "Invalid SLA policy targets.");
    }
    public async Task<object> SavePolicyAsync(Actor actor, string priority, SlaPolicyInput input, CancellationToken cancellationToken)
    {
        ValidatePolicy(priority, input);
        await using var connection = await database.OpenAsync(cancellationToken); await using var transaction = (MySqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var parameters = new Dictionary<string, object?> { ["priority"] = priority, ["response"] = input.ResponseMinutes, ["resolution"] = input.ResolutionMinutes, ["warning"] = input.WarningPercent, ["active"] = input.Active, ["calendar"] = input.CalendarId, ["actor"] = actor.Id };
        var prior = await Database.QueryAsync(connection, "SELECT " + string.Join(',', ColumnCatalog.Columns("sla_policies").Select(c => "`" + c + "`")) + " FROM sla_policies WHERE priority=@priority", parameters, transaction, cancellationToken);
        if (prior.Count == 0) throw new ApiFault(400, "SLA policy not found.");
        if ((await Database.QueryAsync(connection, "SELECT id FROM sla_calendars WHERE id=@calendar AND active=1 AND sql_timezone IS NOT NULL FOR UPDATE", parameters, transaction, cancellationToken)).Count == 0) throw new ApiFault(400, "Select an active SLA calendar.");
        await Database.ExecuteAsync(connection, "UPDATE sla_policies SET response_minutes=@response,resolution_minutes=@resolution,warning_percent=@warning,active=@active,updated_by_user_id=@actor,calendar_id=@calendar,updated_at=UTC_TIMESTAMP(6) WHERE priority=@priority", parameters, transaction, cancellationToken);
        if (input.ApplyToOpenTickets && input.Active)
        {
            await Database.ExecuteAsync(connection, """
                UPDATE ticket_sla s JOIN tickets t ON t.id=s.ticket_id JOIN sla_policies p ON p.priority=t.priority SET  s.policy_id=p.id, s.calendar_id=p.calendar_id, s.target_priority=p.priority, s.response_warning_at=sla_add_business_minutes(p.calendar_id,t.created_at,CAST(CEILING(p.response_minutes*p.warning_percent/100.0) AS SIGNED)), s.response_due_at=sla_add_business_minutes(p.calendar_id,t.created_at,p.response_minutes), s.resolution_warning_at=sla_add_business_minutes(p.calendar_id,t.created_at,CAST(CEILING(p.resolution_minutes*p.warning_percent/100.0) AS SIGNED)), s.resolution_due_at=sla_add_business_minutes(p.calendar_id,t.created_at,p.resolution_minutes), s.paused_at=NULL, s.response_warning_remaining_minutes=NULL, s.response_remaining_minutes=NULL, s.resolution_warning_remaining_minutes=NULL, s.resolution_remaining_minutes=NULL, s.updated_at=UTC_TIMESTAMP(6) WHERE p.priority=@priority AND t.status NOT IN('RESOLVED','CLOSED','CANCELLED');
                UPDATE ticket_sla s JOIN tickets t ON t.id=s.ticket_id SET  s.paused_at=UTC_TIMESTAMP(6), s.response_warning_remaining_minutes=CASE WHEN response_completed_at IS NULL THEN sla_business_minutes_between(s.calendar_id,UTC_TIMESTAMP(6),response_warning_at) END, s.response_remaining_minutes=CASE WHEN response_completed_at IS NULL THEN sla_business_minutes_between(s.calendar_id,UTC_TIMESTAMP(6),response_due_at) END, s.resolution_warning_remaining_minutes=CASE WHEN resolution_completed_at IS NULL THEN sla_business_minutes_between(s.calendar_id,UTC_TIMESTAMP(6),resolution_warning_at) END, s.resolution_remaining_minutes=CASE WHEN resolution_completed_at IS NULL THEN sla_business_minutes_between(s.calendar_id,UTC_TIMESTAMP(6),resolution_due_at) END WHERE t.priority=@priority AND t.status IN('WAITING_FOR_CLIENT','WAITING_FOR_VENDOR','ON_HOLD');
                """, parameters, transaction, cancellationToken);
        }
        await Database.ExecuteAsync(connection, "INSERT INTO audit_logs(actor_user_id,action,entity_type,entity_id,details) VALUES(@actor,'SLA_POLICY.UPDATED','SLA_POLICY',@priority,@details)", new Dictionary<string, object?> { ["actor"] = actor.Id, ["priority"] = priority, ["details"] = JsonSerializer.Serialize(new { before = prior[0], after = input }, JsonSerializerOptions.Web) }, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken); return new { priority };
    }

}
