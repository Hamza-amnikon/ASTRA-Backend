using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Astra.Shared.Data;
using Astra.Shared.Security;
using MySqlConnector;

namespace Astra.Shared.Services;

public sealed class TicketReadService(Database database)
{
    public static readonly string[] StaffRoles = ["INTERN", "TECHNICIAN", "TEAM_LEAD", "MANAGER", "ADMIN"];
    public static readonly string[] Statuses = ["NEW", "OPEN", "IN_PROGRESS", "WAITING_FOR_CLIENT", "WAITING_FOR_VENDOR", "ON_HOLD", "RESOLVED", "CLOSED", "CANCELLED"];
    public static readonly string[] Priorities = ["P1_CRITICAL", "P2_HIGH", "P3_MEDIUM", "P4_LOW"];
    public static readonly string[] SlaStates = ["NOT_TRACKED", "ON_TRACK", "AT_RISK", "BREACHED", "MET", "PAUSED"];
    public static bool Wide(Actor actor) => actor.Roles.Overlaps(["TEAM_LEAD", "MANAGER", "ADMIN"]);
    public static bool Permission(Actor actor, string permission) => actor.Roles.Contains("ADMIN") || actor.Permissions.Contains(permission);
    public static void RequireStaff(Actor actor) { if (!actor.Roles.Overlaps(StaffRoles)) throw new ApiFault(403, "Service-desk access required."); }
    public static Dictionary<string, object?> Parameters(Actor actor)
    {
        var parameters = ScopeSql.Parameters(actor); parameters["actor"] = actor.Id; parameters["wide"] = Wide(actor); parameters["team"] = Permission(actor, "TICKETS_TEAM_VIEW"); parameters["departments"] = JsonSerializer.Serialize(actor.DepartmentIds ?? []); return parameters;
    }
    public static string Access(bool mutation = false) => """
        (@scopeAdmin=1 OR(t.client_id IN(SELECT `value` FROM JSON_TABLE(@scopeClients, '$[*]' COLUMNS(`value` VARCHAR(100) PATH '$')) AS json_values) AND
         (@wide=1 OR t.assigned_user_id=@actor OR EXISTS(SELECT 1 FROM ticket_collaborators tc WHERE tc.ticket_id=t.id AND tc.user_id=@actor)
        """ + (mutation ? "" : """
         OR EXISTS(SELECT 1 FROM help_requests hr WHERE hr.ticket_id=t.id AND hr.status='PENDING' AND(hr.requested_user_id=@actor OR hr.department_id IN(SELECT `value` FROM JSON_TABLE(@departments, '$[*]' COLUMNS(`value` VARCHAR(100) PATH '$')) AS json_values)))
         OR(@team=1 AND(@scopeSiteCount=0 OR t.site_id IN(SELECT `value` FROM JSON_TABLE(@scopeSites, '$[*]' COLUMNS(`value` VARCHAR(100) PATH '$')) AS json_values)))
        """) + ")))";
    public static string Select() => "SELECT " + string.Join(',', ColumnCatalog.Columns("tickets").Select(c => "t.`" + c + "`")) + """
        ,c.name AS client_name,r.name AS requester_name,r.email AS requester_email,s.name AS site_name,
        CASE WHEN parentcat.name IS NULL THEN cat.name ELSE CONCAT(parentcat.name,' → ',cat.name) END AS category_name,
        src.name AS source_name,u.display_name AS assigned_name,creator.display_name AS created_by_name,
        sla.response_warning_at,sla.response_due_at,sla.response_completed_at,sla.resolution_warning_at,sla.resolution_due_at,sla.resolution_completed_at,sla.paused_at,
        CASE WHEN sla.resolution_due_at IS NULL OR t.status='CANCELLED' THEN 'NOT_TRACKED'
         WHEN sla.resolution_completed_at IS NOT NULL AND sla.resolution_completed_at<=sla.resolution_due_at THEN 'MET'
         WHEN sla.paused_at IS NOT NULL THEN 'PAUSED'
         WHEN COALESCE(sla.resolution_completed_at,UTC_TIMESTAMP(6))>=sla.resolution_due_at THEN 'BREACHED'
         WHEN UTC_TIMESTAMP(6)>=sla.resolution_warning_at THEN 'AT_RISK' ELSE 'ON_TRACK' END AS sla_status,
        (SELECT COALESCE(JSON_ARRAYAGG(JSON_OBJECT('id',tag.id,'name',tag.name)),JSON_ARRAY()) FROM ticket_tags tt JOIN tags tag ON tag.id=tt.tag_id WHERE tt.ticket_id=t.id) AS tags
        FROM tickets t JOIN clients c ON c.id=t.client_id JOIN contacts r ON r.id=t.requester_id
        LEFT JOIN sites s ON s.id=t.site_id LEFT JOIN categories cat ON cat.id=t.category_id
        LEFT JOIN categories parentcat ON parentcat.id=cat.parent_id LEFT JOIN ticket_sources src ON src.code=t.source
        LEFT JOIN users u ON u.id=t.assigned_user_id LEFT JOIN users creator ON creator.id=t.created_by_user_id
        LEFT JOIN ticket_sla sla ON sla.ticket_id=t.id
        """;
    public static void Decode(IEnumerable<Dictionary<string, object?>> rows)
    { foreach (var row in rows) if (row.ContainsKey("tags")) row["tags"] = JsonNode.Parse(row["tags"] as string ?? "[]"); }
    public async Task<object> OptionsAsync(Actor actor, CancellationToken cancellationToken)
    {
        RequireStaff(actor); var parameters = Parameters(actor);
        await using var connection = await database.OpenAsync(cancellationToken);
        async Task<List<Dictionary<string, object?>>> Query(string sql) => await Database.QueryAsync(connection, sql, parameters, cancellationToken: cancellationToken);
        var clients = await Query("SELECT id,name FROM clients WHERE status='ACTIVE' AND(@scopeAdmin=1 OR id IN(SELECT `value` FROM JSON_TABLE(@scopeClients, '$[*]' COLUMNS(`value` VARCHAR(100) PATH '$')) AS json_values)) ORDER BY name");
        var contacts = await Query("SELECT id,client_id,name,email FROM contacts WHERE active=1 AND(@scopeAdmin=1 OR client_id IN(SELECT `value` FROM JSON_TABLE(@scopeClients, '$[*]' COLUMNS(`value` VARCHAR(100) PATH '$')) AS json_values)) ORDER BY name");
        var sites = await Query("SELECT id,client_id,name FROM sites WHERE active=1 AND(@scopeAdmin=1 OR(client_id IN(SELECT `value` FROM JSON_TABLE(@scopeClients, '$[*]' COLUMNS(`value` VARCHAR(100) PATH '$')) AS json_values) AND(@scopeSiteCount=0 OR id IN(SELECT `value` FROM JSON_TABLE(@scopeSites, '$[*]' COLUMNS(`value` VARCHAR(100) PATH '$')) AS json_values)))) ORDER BY name");
        var categories = await Query("SELECT c.id,c.parent_id,CASE WHEN p.name IS NULL THEN c.name ELSE CONCAT(p.name,' → ',c.name) END AS name FROM categories c LEFT JOIN categories p ON p.id=c.parent_id WHERE c.active=1 ORDER BY COALESCE(p.name,c.name),p.name,c.name");
        var tags = await Query("SELECT id,name FROM tags WHERE active=1 ORDER BY name");
        var technicians = await Query("SELECT DISTINCT u.id,u.display_name AS name,u.email FROM users u JOIN user_roles ur ON ur.user_id=u.id WHERE u.active=1 AND ur.role_code IN('INTERN','TECHNICIAN','TEAM_LEAD','MANAGER','ADMIN') AND(@scopeAdmin=1 OR EXISTS(SELECT 1 FROM client_users cu WHERE cu.user_id=u.id AND cu.client_id IN(SELECT `value` FROM JSON_TABLE(@scopeClients, '$[*]' COLUMNS(`value` VARCHAR(100) PATH '$')) AS json_values))) ORDER BY name");
        var sources = await Query("SELECT code,name FROM ticket_sources WHERE active=1 ORDER BY sort_order,name"); var departments = await Query("SELECT id,name FROM departments WHERE active=1 ORDER BY name");
        return new { clients, contacts, sites, categories, tags, technicians, sources, departments, statuses = Statuses, priorities = Priorities };
    }
    public async Task<object> DetailAsync(Actor actor, Guid id, CancellationToken cancellationToken)
    {
        RequireStaff(actor); var parameters = Parameters(actor); parameters["id"] = id;
        await using var connection = await database.OpenAsync(cancellationToken);
        var tickets = await Database.QueryAsync(connection, Select() + " WHERE t.id=@id AND " + Access(), parameters, cancellationToken: cancellationToken);
        if (tickets.Count == 0) throw new ApiFault(404, "Ticket not found."); Decode(tickets); parameters["client"] = tickets[0]["client_id"];
        async Task<List<Dictionary<string, object?>>> Query(string sql) => await Database.QueryAsync(connection, sql, parameters, cancellationToken: cancellationToken);
        var comments = await Query("SELECT tc.id,tc.comment_type,tc.body,tc.created_at,u.display_name AS author_name FROM ticket_comments tc JOIN users u ON u.id=tc.author_user_id WHERE tc.ticket_id=@id ORDER BY tc.created_at,tc.id");
        var history = await Query("SELECT h.id,h.action,h.previous_value,h.new_value,h.reason,h.created_at,COALESCE(u.display_name,'System') AS actor_name FROM ticket_history h LEFT JOIN users u ON u.id=h.actor_user_id WHERE h.ticket_id=@id ORDER BY h.created_at,h.id");
        foreach (var row in history) foreach (var column in new[] { "previous_value", "new_value" }) row[column] = row[column] is string value ? JsonNode.Parse(value) : null;
        var attachments = await Query("SELECT a.id,a.file_name,a.content_type,a.size_bytes,a.created_at,a.visibility,u.display_name AS uploaded_by FROM ticket_attachments a JOIN users u ON u.id=a.uploaded_by_user_id WHERE a.ticket_id=@id ORDER BY a.created_at");
        var approvals = await Query("SELECT a.id,a.status,a.reason,a.decision_note,a.created_at,a.decided_at,rq.display_name AS requested_by,dc.display_name AS decided_by FROM ticket_approvals a JOIN users rq ON rq.id=a.requested_by_user_id LEFT JOIN users dc ON dc.id=a.decided_by_user_id WHERE a.ticket_id=@id ORDER BY a.created_at DESC");
        var collaborators = await Query("SELECT tc.user_id,u.display_name AS name,tc.reason,tc.created_at FROM ticket_collaborators tc JOIN users u ON u.id=tc.user_id WHERE tc.ticket_id=@id ORDER BY u.display_name");
        var helpRequests = await Query("SELECT " + string.Join(',', ColumnCatalog.Columns("help_requests").Select(c => "h.`" + c + "`")) + ",rq.display_name AS requested_by,ru.display_name AS requested_user,d.name AS department_name,rs.display_name AS responded_by FROM help_requests h JOIN users rq ON rq.id=h.requested_by_user_id LEFT JOIN users ru ON ru.id=h.requested_user_id LEFT JOIN departments d ON d.id=h.department_id LEFT JOIN users rs ON rs.id=h.responded_by_user_id WHERE h.ticket_id=@id ORDER BY h.created_at DESC");
        var knowledge = await Query("SELECT a.id,a.title,a.visibility FROM ticket_knowledge_links l JOIN knowledge_articles a ON a.id=l.article_id WHERE l.ticket_id=@id AND a.status='PUBLISHED' AND(a.client_id IS NULL OR a.client_id=@client) ORDER BY a.title");
        var problems = actor.Roles.Overlaps(["TECHNICIAN", "TEAM_LEAD", "MANAGER", "ADMIN"]) ? await Query("SELECT p.id,p.problem_number,p.title,p.status FROM problem_tickets pt JOIN problems p ON p.id=pt.problem_id WHERE pt.ticket_id=@id AND " + ScopeSql.Records("p") + " ORDER BY p.updated_at DESC") : [];
        return new { ticket = tickets[0], comments, history, attachments, approvals, collaborators, helpRequests, knowledge, problems };
    }

    public async Task<object> ListAsync(Actor actor, string? q, string? scope, string? status, string? priority, Guid? clientId, Guid? siteId, Guid? assignedUserId, Guid? categoryId, Guid? tagId, string? source, string? slaStatus, string? dashboardFilter, string? from, string? to, int page, int pageSize, CancellationToken cancellationToken)
    {
        RequireStaff(actor); scope = string.IsNullOrWhiteSpace(scope) ? "my" : scope;
        if (scope is not ("my" or "team" or "internal" or "all" or "unassigned") || page is < 1 or > 100000 || pageSize is < 1 or > 100 || (status is not null && !Statuses.Contains(status)) || (priority is not null && !Priorities.Contains(priority)) || (slaStatus is not null && !SlaStates.Contains(slaStatus)) || (dashboardFilter is not null && !new[] { "WAITING", "RESOLVED_RECENTLY", "AWAITING_FIRST_RESPONSE", "PENDING_APPROVAL" }.Contains(dashboardFilter))) throw new ApiFault(400, "Invalid ticket list filters.");
        if (scope == "team" && !Permission(actor, "TICKETS_TEAM_VIEW") || scope is "all" or "unassigned" && !Wide(actor)) throw new ApiFault(403, "Ticket scope is not available.");
        if (scope == "internal" && actor.WorkScope is not ("INTERNAL" or "BOTH" or "ALL")) throw new ApiFault(403, "Internal ticket scope is not available.");
        var query = (q ?? "").Trim(); if (query.Length > 500) throw new ApiFault(400, "Search text is too long.");
        var operators = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        query = Regex.Replace(query, @"\b([a-z]+):(?:""([^""]+)""|(\S+))", match => { var key = match.Groups[1].Value.ToLowerInvariant(); if (!new[] { "client", "status", "assigned", "priority", "source", "category", "tag", "site", "sla" }.Contains(key)) return match.Value; operators[key] = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value; return " "; }).Trim();
        static string EscapeLike(string value) => value.Replace("~", "~~").Replace("%", "~%").Replace("_", "~_");
        var values = Parameters(actor); values["scope"] = scope; values["q"] = "%" + EscapeLike(query) + "%"; values["status"] = status ?? operators.GetValueOrDefault("status"); values["priority"] = priority ?? operators.GetValueOrDefault("priority"); values["client"] = clientId; values["site"] = siteId; values["assignee"] = assignedUserId; values["category"] = categoryId; values["tag"] = tagId; values["source"] = source; values["sla"] = slaStatus ?? operators.GetValueOrDefault("sla"); values["dashboard"] = dashboardFilter; values["offset"] = (page - 1) * pageSize; values["take"] = pageSize; values["from"] = ParseDate(from, "From"); values["to"] = ParseDate(to, "To");
        if ((string?)values["status"] is { } statusValue && !Statuses.Contains(statusValue)) throw new ApiFault(400, "Unknown ticket status."); if ((string?)values["priority"] is { } priorityValue && !Priorities.Contains(priorityValue)) throw new ApiFault(400, "Unknown ticket priority."); if ((string?)values["sla"] is { } slaValue && !SlaStates.Contains(slaValue)) throw new ApiFault(400, "Unknown SLA status.");
        if (operators.TryGetValue("site", out var siteTerm)) values["siteSearch"] = "%" + EscapeLike(siteTerm) + "%"; else values["siteSearch"] = "%%";
        if (operators.TryGetValue("client", out var clientTerm)) values["clientSearch"] = "%" + EscapeLike(clientTerm) + "%"; else values["clientSearch"] = "%%";
        if (operators.TryGetValue("assigned", out var assignedTerm)) values["assignedSearch"] = "%" + EscapeLike(assignedTerm) + "%"; else values["assignedSearch"] = "%%";
        if (operators.TryGetValue("category", out var categoryTerm)) values["categorySearch"] = "%" + EscapeLike(categoryTerm) + "%"; else values["categorySearch"] = "%%";
        if (operators.TryGetValue("source", out var sourceTerm)) values["sourceSearch"] = "%" + EscapeLike(sourceTerm) + "%"; else values["sourceSearch"] = "%%";
        if (operators.TryGetValue("tag", out var tagTerm)) values["tagSearch"] = "%" + EscapeLike(tagTerm) + "%"; else values["tagSearch"] = "%%";
        values["departments"] = JsonSerializer.Serialize(actor.DepartmentIds ?? []);
        var where = """
            WHERE (@q='%%' OR t.ticket_number LIKE @q ESCAPE '~' OR t.subject LIKE @q ESCAPE '~' OR t.description LIKE @q ESCAPE '~' OR t.source LIKE @q ESCAPE '~' OR c.name LIKE @q ESCAPE '~' OR r.name LIKE @q ESCAPE '~' OR r.email LIKE @q ESCAPE '~' OR u.display_name LIKE @q ESCAPE '~' OR src.name LIKE @q ESCAPE '~' OR cat.name LIKE @q ESCAPE '~' OR parentcat.name LIKE @q ESCAPE '~' OR EXISTS(SELECT 1 FROM ticket_comments sc WHERE sc.ticket_id=t.id AND sc.body LIKE @q ESCAPE '~') OR EXISTS(SELECT 1 FROM ticket_tags st JOIN tags sg ON sg.id=st.tag_id WHERE st.ticket_id=t.id AND sg.name LIKE @q ESCAPE '~'))
            AND ((@scope='all' AND @wide=1) OR (@scope='unassigned' AND @wide=1 AND t.assigned_user_id IS NULL) OR (@scope='my' AND (t.assigned_user_id=@actor OR EXISTS(SELECT 1 FROM ticket_collaborators own WHERE own.ticket_id=t.id AND own.user_id=@actor))) OR (@scope='team' AND @team=1 AND (@scopeSiteCount=0 OR t.site_id IN(SELECT `value` FROM JSON_TABLE(@scopeSites, '$[*]' COLUMNS(`value` VARCHAR(100) PATH '$')) AS json_values))) OR (@scope='internal' AND t.client_id='00000000-0000-4000-8000-000000000132' AND (@scopeAdmin=1 OR @scopeSiteCount=0 OR t.site_id IN(SELECT `value` FROM JSON_TABLE(@scopeSites, '$[*]' COLUMNS(`value` VARCHAR(100) PATH '$')) AS json_values)) AND (NOT EXISTS(SELECT 1 FROM department_users du WHERE du.user_id=@actor) OR t.department_id IN(SELECT `value` FROM JSON_TABLE(@departments, '$[*]' COLUMNS(`value` VARCHAR(100) PATH '$')) AS json_values))) OR (@scope NOT IN('all','unassigned','my','team','internal') AND 1=0))
            AND (@scopeAdmin=1 OR t.client_id IN(SELECT `value` FROM JSON_TABLE(@scopeClients, '$[*]' COLUMNS(`value` VARCHAR(100) PATH '$')) AS json_values))
            AND (@status IS NULL OR t.status=@status) AND (@priority IS NULL OR t.priority=@priority) AND (@client IS NULL OR t.client_id=@client) AND (@site IS NULL OR t.site_id=@site) AND (@assignee IS NULL OR t.assigned_user_id=@assignee) AND (@category IS NULL OR t.category_id=@category OR cat.parent_id=@category) AND (@source IS NULL OR t.source=@source) AND (@tag IS NULL OR EXISTS(SELECT 1 FROM ticket_tags tf WHERE tf.ticket_id=t.id AND tf.tag_id=@tag))
            AND (@from IS NULL OR t.created_at>=@from) AND (@to IS NULL OR t.created_at<DATE_ADD(@to, INTERVAL 1 DAY))
            AND (@clientSearch='%%' OR c.name LIKE @clientSearch ESCAPE '~') AND (@assignedSearch='%%' OR COALESCE(u.display_name,'Unassigned') LIKE @assignedSearch ESCAPE '~') AND (@categorySearch='%%' OR cat.name LIKE @categorySearch ESCAPE '~' OR parentcat.name LIKE @categorySearch ESCAPE '~') AND (@sourceSearch='%%' OR t.source LIKE @sourceSearch ESCAPE '~' OR src.name LIKE @sourceSearch ESCAPE '~') AND (@tagSearch='%%' OR EXISTS(SELECT 1 FROM ticket_tags tx JOIN tags tg ON tg.id=tx.tag_id WHERE tx.ticket_id=t.id AND tg.name LIKE @tagSearch ESCAPE '~')) AND (@siteSearch='%%' OR s.name LIKE @siteSearch ESCAPE '~')
            AND (@sla IS NULL OR CASE WHEN sla.resolution_due_at IS NULL OR t.status='CANCELLED' THEN 'NOT_TRACKED' WHEN sla.resolution_completed_at IS NOT NULL AND sla.resolution_completed_at<=sla.resolution_due_at THEN 'MET' WHEN sla.paused_at IS NOT NULL THEN 'PAUSED' WHEN COALESCE(sla.resolution_completed_at,UTC_TIMESTAMP(6))>=sla.resolution_due_at THEN 'BREACHED' WHEN UTC_TIMESTAMP(6)>=sla.resolution_warning_at THEN 'AT_RISK' ELSE 'ON_TRACK' END=@sla)
            AND (@dashboard IS NULL OR CASE @dashboard WHEN 'WAITING' THEN IF(t.status IN('WAITING_FOR_CLIENT','WAITING_FOR_VENDOR'),1,0) WHEN 'RESOLVED_RECENTLY' THEN IF(t.status='RESOLVED' AND t.resolved_at>=DATE_ADD(UTC_TIMESTAMP(6), INTERVAL -7 DAY),1,0) WHEN 'PENDING_APPROVAL' THEN IF(t.approval_status='PENDING',1,0) WHEN 'AWAITING_FIRST_RESPONSE' THEN IF(t.status NOT IN('RESOLVED','CLOSED','CANCELLED') AND t.first_response_at IS NULL AND sla.paused_at IS NULL AND sla_business_minutes_between(sla.calendar_id,t.created_at,UTC_TIMESTAMP(6))>=30,1,0) ELSE 0 END=1)
            AND (@dashboard IS NULL OR @scope<>'my' OR t.assigned_user_id=@actor)
            """;
        if ((DateTimeOffset?)values["from"] is DateTimeOffset start && (DateTimeOffset?)values["to"] is DateTimeOffset end && start > end) throw new ApiFault(400, "From date must be before To date.");
        await using var connection = await database.OpenAsync(cancellationToken);
        var totalRows = await Database.QueryAsync(connection, "SELECT COUNT(*) AS total FROM tickets t JOIN clients c ON c.id=t.client_id JOIN contacts r ON r.id=t.requester_id LEFT JOIN sites s ON s.id=t.site_id LEFT JOIN categories cat ON cat.id=t.category_id LEFT JOIN categories parentcat ON parentcat.id=cat.parent_id LEFT JOIN ticket_sources src ON src.code=t.source LEFT JOIN users u ON u.id=t.assigned_user_id LEFT JOIN users creator ON creator.id=t.created_by_user_id LEFT JOIN ticket_sla sla ON sla.ticket_id=t.id " + where, values, cancellationToken: cancellationToken);
        var rows = await Database.QueryAsync(connection, TicketReadService.Select() + " " + where + " ORDER BY t.updated_at DESC,t.id LIMIT @take OFFSET @offset", values, cancellationToken: cancellationToken);
        Decode(rows); return new { total = Convert.ToInt64(totalRows[0]["total"]), items = rows, page, pageSize };
    }
    private static DateTimeOffset? ParseDate(string? value, string label)
    { if (string.IsNullOrWhiteSpace(value)) return null; if (!DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)) throw new ApiFault(400, $"Invalid {label.ToLowerInvariant()} date."); return parsed; }
    public async Task<object> DashboardAsync(Actor actor, bool requestedAll, CancellationToken cancellationToken)
    {
        RequireStaff(actor); var all = requestedAll && Wide(actor); var parameters = Parameters(actor); parameters["assignee"] = all ? null : actor.Id; parameters["now"] = DateTimeOffset.UtcNow;
        var scope = "(@assignee IS NULL OR t.assigned_user_id=@assignee) AND(@scopeAdmin=1 OR t.client_id IN(SELECT `value` FROM JSON_TABLE(@scopeClients, '$[*]' COLUMNS(`value` VARCHAR(100) PATH '$')) AS json_values))";
        await using var connection = await database.OpenAsync(cancellationToken);
        async Task<List<Dictionary<string, object?>>> Query(string sql) => await Database.QueryAsync(connection, sql, parameters, cancellationToken: cancellationToken);
        static string Count(string predicate, string alias) => "COALESCE(SUM(CASE WHEN " + predicate + " THEN 1 ELSE 0 END),0) AS `" + alias + "`";
        var open = "t.status NOT IN('RESOLVED','CLOSED','CANCELLED')";
        var counts = (await Query("SELECT " + string.Join(',', new[] { Count(open, "open"), Count("t.status='NEW'", "new"), Count("t.status='IN_PROGRESS'", "in_progress"), Count("t.status IN('WAITING_FOR_CLIENT','WAITING_FOR_VENDOR')", "waiting"), Count("t.status='RESOLVED' AND t.resolved_at>=DATE_ADD(@now, INTERVAL -7 DAY)", "recently_resolved"), Count("t.approval_status='PENDING'", "pending_approval"), Count("t.assigned_user_id IS NULL AND " + open, "unassigned"), Count(open + " AND s.paused_at IS NULL AND @now>=s.resolution_warning_at AND @now<s.resolution_due_at", "sla_at_risk"), Count(open + " AND s.paused_at IS NULL AND @now>=s.resolution_due_at", "sla_breached"), Count(open + " AND t.first_response_at IS NULL AND s.paused_at IS NULL AND sla_business_minutes_between(s.calendar_id,t.created_at,@now)>=30", "unattended") }) + " FROM tickets t LEFT JOIN ticket_sla s ON s.ticket_id=t.id WHERE " + scope)).Single();
        var recent = await Query(Select() + " WHERE " + scope + " ORDER BY t.updated_at DESC LIMIT 8 OFFSET 0"); Decode(recent);
        var status = await Query("SELECT t.status AS label,COUNT(*) AS value FROM tickets t WHERE " + scope + " GROUP BY t.status ORDER BY value DESC");
        var priority = await Query("SELECT t.priority AS label,COUNT(*) AS value FROM tickets t WHERE " + scope + " GROUP BY t.priority ORDER BY value DESC");
        var source = await Query("SELECT COALESCE(s.name,t.source) AS label,COUNT(*) AS value FROM tickets t LEFT JOIN ticket_sources s ON s.code=t.source WHERE " + scope + " GROUP BY COALESCE(s.name,t.source) ORDER BY value DESC");
        var trend = await Query("WITH RECURSIVE days AS(SELECT DATE_SUB(DATE(@now), INTERVAL 13 DAY) AS day UNION ALL SELECT DATE_ADD(day,INTERVAL 1 DAY) FROM days WHERE day<DATE(@now)) SELECT DATE_FORMAT(d.day,'%Y-%m-%d') AS day,COALESCE(SUM(CASE WHEN t.created_at>=d.day AND t.created_at<DATE_ADD(d.day, INTERVAL 1 DAY) THEN 1 ELSE 0 END),0) AS created,COALESCE(SUM(CASE WHEN t.resolved_at>=d.day AND t.resolved_at<DATE_ADD(d.day, INTERVAL 1 DAY) THEN 1 ELSE 0 END),0) AS resolved FROM days d LEFT JOIN tickets t ON " + scope + " AND((t.created_at>=d.day AND t.created_at<DATE_ADD(d.day, INTERVAL 1 DAY)) OR(t.resolved_at>=d.day AND t.resolved_at<DATE_ADD(d.day, INTERVAL 1 DAY))) GROUP BY d.day ORDER BY d.day");
        var workload = all ? await Query("SELECT u.display_name AS name," + Count(open, "open") + "," + Count("t.status='RESOLVED' AND t.resolved_at>=DATE_ADD(@now, INTERVAL -30 DAY)", "resolved") + " FROM users u JOIN(SELECT DISTINCT user_id FROM user_roles WHERE role_code IN('INTERN','TECHNICIAN','TEAM_LEAD','MANAGER','ADMIN'))ur ON ur.user_id=u.id LEFT JOIN tickets t ON t.assigned_user_id=u.id AND(@scopeAdmin=1 OR t.client_id IN(SELECT `value` FROM JSON_TABLE(@scopeClients, '$[*]' COLUMNS(`value` VARCHAR(100) PATH '$')) AS json_values)) WHERE u.active=1 GROUP BY u.id,u.display_name ORDER BY `open` DESC,u.display_name") : [];
        return new { counts, recent, charts = new { status, priority, source, trend, workload } };
    }

}
