using System.Globalization;
using Astra.Shared.Data;
using Astra.Shared.Security;

namespace ReportService.API.Services;

public sealed class ServiceReportService(Database database)
{
    public static (DateTimeOffset Start, DateTimeOffset End) Range(string from, string to)
    {
        if (!DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) || !DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var through) || through < start || through.DayNumber - start.DayNumber > 366 || through == DateOnly.MaxValue)
            throw new ApiFault(400, "Choose a date range of no more than one year.");
        return (new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), new DateTimeOffset(through.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
    }

    public async Task<object> ReportAsync(Actor actor, string from, string to, Guid? clientId, CancellationToken cancellationToken = default)
    {
        var admin = actor.Roles.Contains("ADMIN");
        if (!admin && !actor.Permissions.Contains("REPORTS_VIEW")) throw new ApiFault(403, "Permission required.");
        var range = Range(from, to);
        if (clientId is Guid client && !admin && !actor.ClientIds.Contains(client)) throw new ApiFault(403, "Organization is not assigned.");
        var parameters = ScopeSql.Parameters(actor);
        parameters["client"] = clientId;
        parameters["start"] = range.Start;
        parameters["end"] = range.End;
        var scope = ScopeSql.Records("t") + " AND (@client IS NULL OR t.client_id=@client)";
        await using var connection = await database.OpenAsync(cancellationToken);
        var tickets = (await Database.QueryAsync(connection, $"""
            SELECT CAST(COALESCE(SUM(CASE WHEN t.created_at>=@start AND t.created_at<@end THEN 1 ELSE 0 END),0) AS SIGNED) AS created,
              CAST(COALESCE(SUM(CASE WHEN t.resolved_at>=@start AND t.resolved_at<@end THEN 1 ELSE 0 END),0) AS SIGNED) AS resolved,
              CAST(COALESCE(SUM(CASE WHEN t.status NOT IN ('RESOLVED','CLOSED','CANCELLED') THEN 1 ELSE 0 END),0) AS SIGNED) AS open_now,
              CAST(CAST(ROUND(AVG(CASE WHEN t.resolved_at>=@start AND t.resolved_at<@end THEN TIMESTAMPDIFF(MICROSECOND,t.created_at,t.resolved_at)/3600000000.0 ELSE NULL END),1) AS decimal(18,1)) AS CHAR) AS average_resolution_hours
            FROM tickets t WHERE {scope}
            """, parameters, cancellationToken: cancellationToken)).Single();
        var sources = await Database.QueryAsync(connection, $"SELECT t.source,COUNT(*) AS count FROM tickets t WHERE {scope} AND t.created_at>=@start AND t.created_at<@end GROUP BY t.source ORDER BY count DESC,t.source", parameters, cancellationToken: cancellationToken);
        var changes = admin || actor.ChangeAccess != "NONE" ? await Database.QueryAsync(connection, $"SELECT t.status,COUNT(*) AS count FROM changes t WHERE {scope} AND t.created_at>=@start AND t.created_at<@end GROUP BY t.status ORDER BY count DESC,t.status", parameters, cancellationToken: cancellationToken) : [];
        var knowledge = (await Database.QueryAsync(connection, $"SELECT COUNT(*) AS article_links FROM ticket_knowledge_links l JOIN tickets t ON t.id=l.ticket_id WHERE {scope} AND l.created_at>=@start AND l.created_at<@end", parameters, cancellationToken: cancellationToken)).Single();
        var clients = await Database.QueryAsync(connection, "SELECT id,name FROM clients WHERE @scopeAdmin=1 OR id IN (SELECT `value` FROM JSON_TABLE(@scopeClients, '$[*]' COLUMNS(`value` VARCHAR(100) PATH '$')) AS json_values) ORDER BY name", parameters, cancellationToken: cancellationToken);
        return new { from, to, clientId, tickets, sources, changes, knowledge, clients };
    }


}
