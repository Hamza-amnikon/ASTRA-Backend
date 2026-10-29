using System.Text.Json;
using System.Text.RegularExpressions;
using MySqlConnector;

public static partial class FeatureApis
{
    private static void MapHelp(RouteGroupBuilder api)
    {
        api.MapPost("/help/ask", async (JsonElement b, HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.SearchHelp(Actor(c), b, t)));
        api.MapGet("/help/web-status", async (HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.HelpWebStatus(Actor(c), t)));
        api.MapPost("/help/web-search", async (JsonElement b, HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.HelpWebSearch(Actor(c), b, t)));
        api.MapPost("/help/feedback", async (JsonElement b, HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.HelpFeedback(Actor(c), b, false, t)));
        api.MapPost("/help/resolution-feedback", async (JsonElement b, HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.HelpFeedback(Actor(c), b, true, t)));
        api.MapPost("/internal/tickets/{id:guid}/help", async (string id, JsonElement b, HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.TicketHelp(Actor(c), id, null, b, t)));
        api.MapPatch("/internal/tickets/{ticket:guid}/help/{id:guid}", async (string ticket, string id, JsonElement b, HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.TicketHelp(Actor(c), ticket, id, b, t)));
    }
}
public sealed record HelpHit(string SourceType, string Id, string Title, string Summary, int Score, string[] MatchReasons, string Href, string? Resolution = null, string[]? Procedure = null);
public sealed partial class MySqlStore
{
    private static async Task InsertNotification(MySqlConnection c, MySqlTransaction tx, string user, string ticket, string type, string title, string body, string key, CancellationToken ct)
    {
        await FExecute(c, tx, "INSERT IGNORE INTO notifications(user_id,ticket_id,type,title,body,event_key) SELECT @user,@ticket,@type,@title,@body,@key FROM users u WHERE u.id=@user AND u.active=1 AND (u.access_expires_at IS NULL OR u.access_expires_at>UTC_TIMESTAMP(6)) AND (@type<>'PUBLIC_REPLY' OR COALESCE((SELECT public_reply FROM notification_preferences p WHERE p.user_id=u.id),1)=1)", ct, ("user", user), ("ticket", ticket), ("type", type), ("title", title), ("body", body), ("key", key));
    }
    private static string SafeHelpQuery(string input) => Regex.Replace(Regex.Replace(input, @"https?://\S+|[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}|\b\d{1,3}(?:\.\d{1,3}){3}\b|\b[0-9a-fA-F]{8}-[0-9a-fA-F-]{27,}\b|\b[A-Z]{2,8}-\d{3,10}\b", " ", RegexOptions.IgnoreCase), @"\s+", " ").Trim();
    public async Task<object> SearchHelp(string actor, JsonElement b, CancellationToken ct)
    {
        var query = FText(JString(b, "question"), "Question", 2, 500);
        var topics = await GetHelpTopics(actor, ct) ?? throw new UnauthorizedAccessException();
        await SupportRate("help-search:" + actor, 30, 60, ct);
        var tokens = Regex.Matches(query.ToLowerInvariant(), @"[\p{L}\p{N}]{2,}").Select(m => m.Value).Where(x => !new[] { "the", "and", "with", "for", "how", "can", "this", "that", "have", "please", "help" }.Contains(x)).Distinct().Take(12).ToArray();
        if (tokens.Length == 0)
            tokens = [query.ToLowerInvariant()];
        var hits = new List<HelpHit>();
        int Score(string title, string body) => tokens.Sum(token => title.Contains(token, StringComparison.OrdinalIgnoreCase) ? 5 : body.Contains(token, StringComparison.OrdinalIgnoreCase) ? 2 : 0);
        foreach (var raw in topics)
        {
            var topic = JsonSerializer.SerializeToElement(raw);
            var title = JString(topic, "title");
            var summary = JString(topic, "summary");
            var steps = JArray(topic, "steps").Select(x => x.GetString() ?? "").ToArray();
            var score = Score(title, summary + " " + string.Join(' ', steps));
            if (score > 0)
                hits.Add(new("guide", JString(topic, "id"), title, summary, score, ["Guide text match"], JString(topic, "href"), Procedure: steps));
        }
        var articles = new Dictionary<string, Dictionary<string, object?>>();
        foreach (var token in tokens.Take(4))
        {
            var raw = await ListKnowledge(actor, token, 1, null, ct);
            if (raw is null)
                continue;
            var data = JsonSerializer.SerializeToElement(raw);
            foreach (var article in data.GetProperty("items").EnumerateArray())
            {
                var row = JsonSerializer.Deserialize<Dictionary<string, object?>>(article.GetRawText())!;
                articles[JString(article, "id")] = row;
            }
        }
        foreach (var (id, row) in articles)
        {
            var title = Convert.ToString(row["title"]) ?? "";
            var body = Convert.ToString(row["body"]) ?? "";
            var score = Score(title, body);
            if (score > 0)
                hits.Add(new("knowledge", id, title, body[..Math.Min(body.Length, 280)], score + 3, ["Knowledge text match"], "/knowledge?article=" + id));
        }
        var roles = await FQuery("SELECT role_code FROM user_roles WHERE user_id=@actor", ct, ("actor", actor));
        var staff = roles.Any(x => (string?)x["role_code"] is "INTERN" or "TECHNICIAN" or "TEAM_LEAD" or "MANAGER" or "ADMIN");
        if (staff)
        {
            var tickets = new Dictionary<string, Dictionary<string, object?>>();
            foreach (var token in tokens.Take(3))
            {
                var scope = roles.Any(x => (string?)x["role_code"] is "ADMIN" or "MANAGER" or "TEAM_LEAD") ? "all" : await HasPermission(actor, "TICKETS_TEAM_VIEW", null, ct) ? "team" : "my";
                var list = await ListTickets(actor, scope, "RESOLVED", token, 1, 10, ct);
                foreach (var row in list.Items)
                    tickets[(string)row["id"]!] = row;
            }
            foreach (var (id, row) in tickets.Take(5))
            {
                var title = (string)row["subject"]!;
                var description = (string)row["description"]!;
                var detail = await GetTicketDetail(actor, id, ct);
                if (detail is null)
                    continue;
                var data = JsonSerializer.SerializeToElement(detail);
                var comments = data.GetProperty("comments").EnumerateArray().Select(x => JString(x, "body")).ToArray();
                var score = Score(title, description + " " + string.Join(' ', comments));
                if (score > 0)
                    hits.Add(new("ticket", id, $"{row["ticket_number"]} · {title}", description[..Math.Min(description.Length, 240)], score + 4, ["Resolved ticket text match"], "/operations/tickets/" + id, comments.LastOrDefault(x => Regex.IsMatch(x, "resolv|fixed|restored|working|cleared|reset", RegexOptions.IgnoreCase))));
            }
        }
        var results = hits.OrderByDescending(x => x.Score).ThenBy(x => x.Title).Take(8).ToArray();
        var safe = SafeHelpQuery(query);
        var webNote = "Local guidance is shown. Approved web search requires a configured search provider.";
        if (results.Length == 0 && safe.Length >= 3 && roles.Any(x => (string?)x["role_code"] is "ADMIN" or "MANAGER" or "TEAM_LEAD" or "TECHNICIAN"))
        {
            try
            {
                if ((await HelpWebStatus(actor, ct)).Enabled)
                {
                    var raw = JsonSerializer.SerializeToElement(await HelpWebSearch(actor, JsonSerializer.SerializeToElement(new
                    {
                        query = safe
                    }), ct));
                    var web = raw.GetProperty("results").Deserialize<WebGuidanceResult[]>() ?? [];
                    results = web.Take(5).Select(x => new HelpHit("web", x.Href, x.Title, string.Join(' ', x.Excerpts), 1, ["Search provider result"], x.Href)).ToArray();
                    webNote = JString(raw, "note");
                }
            }
            catch (FeatureFailure) { webNote = "Local results are shown. The configured web search provider was unavailable."; }
        }
        await using var c = CreateConnection();
        await c.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await AddAudit(c, tx, actor, "HELP.SEARCH", "HELP", null, new
        {
            query = safe,
            resultCount = results.Length
        }, ct);
        await tx.CommitAsync(ct);
        return new
        {
            answer = results.Length > 0 ? "I found related guidance and records within your access." : "No local match was found. Try a product name, symptom, or error message.",
            normalizedQuery = string.Join(' ', tokens),
            detectedProducts = Array.Empty<string>(),
            detectedSymptoms = Array.Empty<string>(),
            results,
            suggestions = results.Length > 0 ? Array.Empty<string>() : new[] { "Include the affected product and the exact error message." },
            webNote
        };
    }
    public async Task<object> HelpFeedback(string actor, JsonElement b, bool resolution, CancellationToken ct)
    {
        if (await GetHelpTopics(actor, ct) is null)
            throw new UnauthorizedAccessException();
        var result = FText(JString(b, "resultId"), "Result", 1, 200);
        var key = resolution ? "resolved" : "helpful";
        if (!b.TryGetProperty(key, out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ArgumentException("Choose a feedback response.");
        await SupportRate("help-feedback:" + actor, 60, 60, ct);
        await using var c = CreateConnection();
        await c.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await AddAudit(c, tx, actor, resolution ? "HELP.RESOLUTION_FEEDBACK" : "HELP.SEARCH_FEEDBACK", "HELP", null, new
        {
            query = SafeHelpQuery(FText(JString(b, "query"), "Query", 0, 500)),
            resultId = result,
            response = value.GetBoolean()
        }, ct);
        await tx.CommitAsync(ct);
        return new
        {
            ok = true
        };
    }
    public async Task<object> TicketHelp(string actor, string ticket, string? request, JsonElement b, CancellationToken ct)
    {
        var detail = await GetTicketDetail(actor, ticket, ct) ?? throw new KeyNotFoundException();
        var roles = await FQuery("SELECT role_code FROM user_roles WHERE user_id=@actor", ct, ("actor", actor));
        if (!roles.Any(x => (string?)x["role_code"] is "INTERN" or "TECHNICIAN" or "TEAM_LEAD" or "MANAGER" or "ADMIN"))
            throw new UnauthorizedAccessException();
        var admin = roles.Any(x => Equals(x["role_code"], "ADMIN"));
        await using var c = CreateConnection();
        await c.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var row = (await FRows(c, tx, "SELECT * FROM tickets WHERE id=@id FOR UPDATE", ct, ("id", ticket))).First();
        var id = request ?? Guid.NewGuid().ToString("D");
        string status;
        if (request is null)
        {
            if (!admin && !roles.Any(x => (string?)x["role_code"] is "MANAGER" or "TEAM_LEAD") && !Equals(row["assigned_user_id"], actor) && (await FRows(c, tx, "SELECT user_id FROM ticket_collaborators WHERE ticket_id=@ticket AND user_id=@actor", ct, ("ticket", ticket), ("actor", actor))).Count == 0)
                throw new UnauthorizedAccessException();
            var user = JOptional(b, "requestedUserId");
            var department = JOptional(b, "departmentId");
            if (user is null && department is null)
                throw new ArgumentException("Choose a person or department.");
            var expertise = FText(JString(b, "expertiseNeeded"), "Expertise needed", 3, 2000);
            if (user is not null)
            {
                user = FGuid(user, "staff member");
                if (user == actor || (await FRows(c, tx, "SELECT u.id FROM users u WHERE u.id=@user AND u.active=1 AND (u.access_expires_at IS NULL OR u.access_expires_at>UTC_TIMESTAMP(6)) AND EXISTS(SELECT 1 FROM user_roles r WHERE r.user_id=u.id AND r.role_code IN ('INTERN','TECHNICIAN','TEAM_LEAD','MANAGER','ADMIN')) AND (EXISTS(SELECT 1 FROM user_roles r WHERE r.user_id=u.id AND r.role_code='ADMIN') OR (EXISTS(SELECT 1 FROM client_users cu WHERE cu.user_id=u.id AND cu.client_id=@client) AND (NOT EXISTS(SELECT 1 FROM user_sites us WHERE us.user_id=u.id) OR EXISTS(SELECT 1 FROM user_sites us WHERE us.user_id=u.id AND us.site_id=@site))))", ct, ("user", user), ("client", row["client_id"]), ("site", row["site_id"]))).Count == 0)
                    throw new ArgumentException("Choose another active staff member with client and site access.");
            }
            if (department is not null)
            {
                department = FGuid(department, "department");
                if ((await FRows(c, tx, "SELECT id FROM departments WHERE id=@id AND active=1", ct, ("id", department))).Count == 0)
                    throw new ArgumentException("Choose an active department.");
            }
            await FExecute(c, tx, "INSERT INTO help_requests(id,ticket_id,requested_by_user_id,requested_user_id,department_id,expertise_needed) VALUES(@id,@ticket,@actor,@user,@department,@expertise)", ct, ("id", id), ("ticket", ticket), ("actor", actor), ("user", user), ("department", department), ("expertise", expertise));
            var recipients = await FRows(c, tx, "SELECT DISTINCT u.id FROM users u WHERE u.active=1 AND u.id<>@actor AND (u.id=@user OR EXISTS(SELECT 1 FROM department_users du WHERE du.user_id=u.id AND du.department_id=@department))", ct, ("actor", actor), ("user", user), ("department", department));
            foreach (var person in recipients)
                await InsertNotification(c, tx, (string)person["id"]!, ticket, "HELP_REQUESTED", $"Help requested for {row["ticket_number"]}", expertise, "help:" + id + ":" + person["id"], ct);
            status = "PENDING";
        }
        else
        {
            status = JChoice(b, "status", "ACCEPTED", "DECLINED", "COMPLETED");
            var note = FText(JString(b, "note"), "Response note", 0, 2000);
            var help = (await FRows(c, tx, "SELECT * FROM help_requests WHERE id=@id AND ticket_id=@ticket FOR UPDATE", ct, ("id", id), ("ticket", ticket))).FirstOrDefault() ?? throw new KeyNotFoundException();
            if (!Equals(help["status"], "PENDING"))
                throw new InvalidOperationException("This help request has already been answered.");
            if (!admin && !Equals(help["requested_user_id"], actor) && (await FRows(c, tx, "SELECT user_id FROM department_users WHERE user_id=@actor AND department_id=@department", ct, ("actor", actor), ("department", help["department_id"]))).Count == 0)
                throw new UnauthorizedAccessException();
            await FExecute(c, tx, "UPDATE help_requests SET status=@status,responded_by_user_id=@actor,response_note=@note,responded_at=UTC_TIMESTAMP(6) WHERE id=@id", ct, ("status", status), ("actor", actor), ("note", note), ("id", id));
            if (status == "ACCEPTED")
                await FExecute(c, tx, "INSERT INTO ticket_collaborators(ticket_id,user_id,added_by_user_id,reason) VALUES(@ticket,@actor,@actor,'Accepted help request') ON DUPLICATE KEY UPDATE reason='Accepted help request'", ct, ("ticket", ticket), ("actor", actor));
            await InsertNotification(c, tx, (string)help["requested_by_user_id"]!, ticket, "HELP_ACCEPTED", $"{row["ticket_number"]} help request {status.ToLowerInvariant()}", (string)row["subject"]!, "help-decision:" + id, ct);
        }
        await FExecute(c, tx, "INSERT INTO ticket_history(ticket_id,actor_user_id,action,new_value) VALUES(@ticket,@actor,@action,JSON_OBJECT('helpRequestId',@id,'status',@status))", ct, ("ticket", ticket), ("actor", actor), ("action", request is null ? "TICKET.HELP_REQUESTED" : "TICKET.HELP_REQUEST_UPDATED"), ("id", id), ("status", status));
        await AddAudit(c, tx, actor, "TICKET.HELP_UPDATED", "TICKET", ticket, new
        {
            helpRequestId = id,
            status
        }, ct);
        await tx.CommitAsync(ct);
        return new
        {
            id,
            status
        };
    }
}
