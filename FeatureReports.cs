using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using Microsoft.AspNetCore.Http;

public static partial class FeatureApis
{
    private static void MapReports(RouteGroupBuilder api)
    {
        api.MapGet("/service-reports", async (HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.ServicePerformance(Actor(c), c.Request.Query["from"], c.Request.Query["to"], c.Request.Query["clientId"], t)));
        api.MapGet("/reports/technicians", async (HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.TechnicianReport(Actor(c), c.Request.Query["from"], c.Request.Query["to"], c.Request.Query["technicianId"], t)));
        api.MapGet("/reports/technicians/export", async (HttpContext c, MySqlStore s, CancellationToken t) =>
        {
            var report = await s.TechnicianReport(Actor(c), c.Request.Query["from"], c.Request.Query["to"], c.Request.Query["technicianId"], t);
            var columns = new[] { ("name", "Technician"), ("email", "Email"), ("received", "Tickets received"), ("transferred_in", "Transferred in"), ("transferred_out", "Transferred out"), ("worked", "Tickets worked"), ("resolved", "Resolved"), ("open", "Open now"), ("sla_met", "SLA met"), ("sla_breached", "SLA breached") };
            var data = new List<object?[]> { new object?[] { "Period (UTC)", report.From, report.To }, columns.Select(x => (object?)x.Item2).ToArray() };
            data.AddRange(report.Rows.Select(r => columns.Select(x => r.GetValueOrDefault(x.Item1)).ToArray()));
            if (report.Tickets.Count > 0)
            {
                var keys = new[] { "ticket_number", "subject", "client_name", "requester_name", "category_name", "priority", "status", "source", "assigned_name", "assigned_at", "resolved_at", "sla_result" };
                data.Add([]);
                data.Add(keys.Cast<object?>().ToArray());
                data.AddRange(report.Tickets.Select(r => keys.Select(x => r.GetValueOrDefault(x)).ToArray()));
            }
            var format = c.Request.Query["format"].ToString();
            return format switch
            {
                "excel" => Results.File(ReportFiles.Xlsx(data), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "astra-technician-report.xlsx"),
                "csv" or "" => Results.File(Encoding.UTF8.GetBytes("\uFEFF" + ReportFiles.Csv(data)), "text/csv; charset=utf-8", "astra-technician-report.csv"),
                _ => throw new ArgumentException("Choose CSV or Excel format.")
            };
        });
    }
}

public sealed record TechnicianReportResult(string From, string To, string? TechnicianId, object Technicians, List<Dictionary<string, object?>> Rows, List<Dictionary<string, object?>> Tickets);

public sealed partial class MySqlStore
{
    private static string FScope(string alias) => $"(@admin=1 OR (EXISTS(SELECT 1 FROM client_users cu WHERE cu.user_id=@actor AND cu.client_id={alias}.client_id) AND (NOT EXISTS(SELECT 1 FROM user_sites us WHERE us.user_id=@actor) OR EXISTS(SELECT 1 FROM user_sites us WHERE us.user_id=@actor AND us.site_id={alias}.site_id))))";
    private async Task<bool> RequireReport(string actor, CancellationToken ct)
    {
        var admin = await IsAdministrator(actor, ct);
        if (!admin && !await HasPermission(actor, "REPORTS_VIEW", new HashSet<string>(["MANAGER", "TEAM_LEAD"], StringComparer.Ordinal), ct))
            throw new UnauthorizedAccessException();
        return admin;
    }
    private static (DateTime Start, DateTime End, string From, string To) FPeriod(string? from, string? to)
    {
        if (!DateTime.TryParse(from, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var start) || !DateTime.TryParse(to, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var end))
            throw new ArgumentException("Choose a valid reporting period.");
        if (to!.Length == 10)
            end = end.Date.AddDays(1);
        if (end <= start || end - start > TimeSpan.FromDays(367))
            throw new ArgumentException("Choose a reporting period of no more than one year.");
        return (start, end, from!, to!);
    }
    public async Task<object> ServicePerformance(string actor, string? from, string? to, string? clientId, CancellationToken ct)
    {
        var admin = await RequireReport(actor, ct);
        var period = FPeriod(from, to);
        clientId = string.IsNullOrEmpty(clientId) ? null : FGuid(clientId, "client");
        var clients = await ListClients(actor, ct);
        if (clientId is not null && !clients.Any(x => Equals(x["id"], clientId)))
            throw new UnauthorizedAccessException();
        var args = new (string, object?)[] { ("actor", actor), ("admin", admin), ("client", clientId), ("start", period.Start), ("end", period.End) };
        var scope = FScope("t") + " AND (@client IS NULL OR t.client_id=@client)";
        var totals = (await FQuery($"SELECT COALESCE(SUM(t.created_at>=@start AND t.created_at<@end),0) AS created,COALESCE(SUM(t.resolved_at>=@start AND t.resolved_at<@end),0) AS resolved,COALESCE(SUM(t.status NOT IN ('RESOLVED','CLOSED','CANCELLED')),0) AS open_now,CAST(ROUND(AVG(CASE WHEN t.resolved_at>=@start AND t.resolved_at<@end THEN TIMESTAMPDIFF(SECOND,t.created_at,t.resolved_at)/3600.0 END),1) AS CHAR) AS average_resolution_hours FROM tickets t WHERE {scope}", ct, args))[0];
        var sources = await FQuery($"SELECT t.source,COUNT(*) AS count FROM tickets t WHERE {scope} AND t.created_at>=@start AND t.created_at<@end GROUP BY t.source ORDER BY count DESC", ct, args);
        var changeAccess = await FQuery("SELECT change_access FROM users WHERE id=@id", ct, ("id", actor));
        var changes = admin || changeAccess.Any(x => (string?)x["change_access"] != "NONE") ? await FQuery($"SELECT t.status,COUNT(*) AS count FROM changes t WHERE {scope} AND t.created_at>=@start AND t.created_at<@end GROUP BY t.status ORDER BY count DESC", ct, args) : [];
        var knowledge = (await FQuery($"SELECT COUNT(*) AS article_links FROM ticket_knowledge_links l JOIN tickets t ON t.id=l.ticket_id WHERE {scope} AND l.created_at>=@start AND l.created_at<@end", ct, args))[0];
        return new
        {
            from = period.From,
            to = period.To,
            clientId,
            tickets = totals,
            sources,
            changes,
            knowledge,
            clients
        };
    }
    public async Task<TechnicianReportResult> TechnicianReport(string actor, string? from, string? to, string? technicianId, CancellationToken ct)
    {
        var admin = await RequireReport(actor, ct);
        var period = FPeriod(from, to);
        technicianId = string.IsNullOrEmpty(technicianId) ? null : FGuid(technicianId, "technician");
        var technicians = await FQuery("SELECT DISTINCT u.id,u.display_name AS name,u.email,u.active FROM users u JOIN user_roles ur ON ur.user_id=u.id WHERE ur.role_code IN ('TECHNICIAN','TEAM_LEAD','MANAGER','ADMIN') AND (@admin=1 OR EXISTS(SELECT 1 FROM client_users own JOIN client_users other ON other.client_id=own.client_id WHERE own.user_id=@actor AND other.user_id=u.id)) ORDER BY u.display_name", ct, ("actor", actor), ("admin", admin));
        if (technicianId is not null && !technicians.Any(x => Equals(x["id"], technicianId)))
            throw new ArgumentException("Selected technician is not available in your scope.");
        var args = new (string, object?)[] { ("actor", actor), ("admin", admin), ("technician", technicianId), ("start", period.Start), ("end", period.End) };
        var scope = FScope("t");
        var rows = await FQuery($"""
            WITH staff AS (SELECT DISTINCT u.id,u.display_name,u.email FROM users u JOIN user_roles r ON r.user_id=u.id WHERE r.role_code IN ('TECHNICIAN','TEAM_LEAD','MANAGER','ADMIN') AND (@technician IS NULL OR u.id=@technician) AND (@admin=1 OR EXISTS(SELECT 1 FROM client_users own JOIN client_users other ON other.client_id=own.client_id WHERE own.user_id=@actor AND other.user_id=u.id))),
            assignments AS (SELECT a.assigned_user_id AS user_id,COUNT(*) AS received,SUM(a.previous_user_id IS NOT NULL) AS transferred_in FROM ticket_assignments a JOIN tickets t ON t.id=a.ticket_id WHERE {scope} AND a.created_at>=@start AND a.created_at<@end GROUP BY a.assigned_user_id),
            transfers_out AS (SELECT a.previous_user_id AS user_id,COUNT(*) AS transferred_out FROM ticket_assignments a JOIN tickets t ON t.id=a.ticket_id WHERE {scope} AND a.previous_user_id IS NOT NULL AND a.created_at>=@start AND a.created_at<@end GROUP BY a.previous_user_id),
            work AS (SELECT c.author_user_id AS user_id,COUNT(DISTINCT c.ticket_id) AS worked FROM ticket_comments c JOIN tickets t ON t.id=c.ticket_id WHERE {scope} AND c.created_at>=@start AND c.created_at<@end GROUP BY c.author_user_id),
            resolved AS (SELECT h.actor_user_id AS user_id,COUNT(*) AS resolved,COALESCE(SUM(s.resolution_completed_at<=s.resolution_due_at),0) AS sla_met,COALESCE(SUM(s.resolution_completed_at>s.resolution_due_at),0) AS sla_breached FROM ticket_history h JOIN tickets t ON t.id=h.ticket_id LEFT JOIN ticket_sla s ON s.ticket_id=t.id WHERE {scope} AND h.action='TICKET.STATUS_CHANGED' AND JSON_UNQUOTE(h.new_value)='RESOLVED' AND h.created_at>=@start AND h.created_at<@end GROUP BY h.actor_user_id),
            open_now AS (SELECT t.assigned_user_id AS user_id,COUNT(*) AS open FROM tickets t WHERE {scope} AND t.status NOT IN ('RESOLVED','CLOSED','CANCELLED') GROUP BY t.assigned_user_id)
            SELECT s.id,s.display_name AS name,s.email,COALESCE(a.received,0) AS received,COALESCE(a.transferred_in,0) AS transferred_in,COALESCE(o.transferred_out,0) AS transferred_out,COALESCE(w.worked,0) AS worked,COALESCE(r.resolved,0) AS resolved,COALESCE(n.open,0) AS open,COALESCE(r.sla_met,0) AS sla_met,COALESCE(r.sla_breached,0) AS sla_breached FROM staff s LEFT JOIN assignments a ON a.user_id=s.id LEFT JOIN transfers_out o ON o.user_id=s.id LEFT JOIN work w ON w.user_id=s.id LEFT JOIN resolved r ON r.user_id=s.id LEFT JOIN open_now n ON n.user_id=s.id ORDER BY s.display_name
            """, ct, args);
        var tickets = technicianId is null ? [] : await FQuery($"""
            SELECT t.id,t.ticket_number,t.subject,c.name AS client_name,r.name AS requester_name,cat.name AS category_name,t.priority,t.status,t.source,u.display_name AS assigned_name,t.created_at,t.resolved_at,
            (SELECT MIN(a.created_at) FROM ticket_assignments a WHERE a.ticket_id=t.id AND a.assigned_user_id=@technician AND a.created_at>=@start AND a.created_at<@end) AS assigned_at,
            CASE WHEN s.resolution_due_at IS NULL THEN 'NOT_TRACKED' WHEN s.resolution_completed_at<=s.resolution_due_at THEN 'MET' WHEN s.resolution_completed_at>s.resolution_due_at THEN 'BREACHED' WHEN s.paused_at IS NOT NULL THEN 'PAUSED' WHEN UTC_TIMESTAMP(6)>=s.resolution_due_at THEN 'BREACHED' WHEN UTC_TIMESTAMP(6)>=s.resolution_warning_at THEN 'AT_RISK' ELSE 'ON_TRACK' END AS sla_result
            FROM tickets t JOIN clients c ON c.id=t.client_id JOIN contacts r ON r.id=t.requester_id LEFT JOIN categories cat ON cat.id=t.category_id LEFT JOIN users u ON u.id=t.assigned_user_id LEFT JOIN ticket_sla s ON s.ticket_id=t.id
            WHERE {scope} AND (t.assigned_user_id=@technician OR EXISTS(SELECT 1 FROM ticket_assignments a WHERE a.ticket_id=t.id AND (a.assigned_user_id=@technician OR a.previous_user_id=@technician) AND a.created_at>=@start AND a.created_at<@end) OR EXISTS(SELECT 1 FROM ticket_comments tc WHERE tc.ticket_id=t.id AND tc.author_user_id=@technician AND tc.created_at>=@start AND tc.created_at<@end) OR EXISTS(SELECT 1 FROM ticket_history h WHERE h.ticket_id=t.id AND h.actor_user_id=@technician AND h.created_at>=@start AND h.created_at<@end)) ORDER BY t.updated_at DESC,t.id
            """, ct, args);
        return new(period.From, period.To, technicianId, technicians, rows, tickets);
    }
}

internal static class ReportFiles
{
    internal static string Text(object? value) => value is DateTime date ? date.ToUniversalTime().ToString("O") : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
    internal static string Csv(IEnumerable<object?[]> rows) => string.Join("\r\n", rows.Select(r => string.Join(",", r.Select(v => { var text = Text(v); if (text.TrimStart().FirstOrDefault() is '=' or '+' or '-' or '@' || text.StartsWith('\t') || text.StartsWith('\r')) text = "'" + text; return "\"" + text.Replace("\"", "\"\"") + "\""; }))));
    internal static byte[] Xlsx(IEnumerable<object?[]> rows)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            void Add(string name, string xml)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(xml);
            }
            Add("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>");
            Add("_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
            Add("xl/workbook.xml", "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Technician report\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            Add("xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>");
            var xml = new StringBuilder("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
            foreach (var row in rows)
            {
                xml.Append("<row>");
                foreach (var value in row)
                    xml.Append("<c t=\"inlineStr\"><is><t xml:space=\"preserve\">").Append(SecurityElement.Escape(new string(Text(value).Where(c => !char.IsControl(c) || c is '\t' or '\n' or '\r').ToArray()))).Append("</t></is></c>");
                xml.Append("</row>");
            }
            xml.Append("</sheetData></worksheet>");
            Add("xl/worksheets/sheet1.xml", xml.ToString());
        }
        return output.ToArray();
    }
}
