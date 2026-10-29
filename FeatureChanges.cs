using System.Text.Json;
using System.Security.Cryptography;
using MySqlConnector;

public static partial class FeatureApis
{
    private static void MapChanges(RouteGroupBuilder api)
    {
        api.MapGet("/changes/options", async (HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.ChangeOptions(Actor(c), t)));
        api.MapGet("/changes/dashboard", async (HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.ChangeDashboard(Actor(c), t)));
        api.MapGet("/changes/calendar", async (HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.ChangeCalendar(Actor(c), c.Request.Query["from"], c.Request.Query["to"], t)));
        api.MapGet("/changes", async (HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.ChangeList(Actor(c), c.Request.Query, t)));
        api.MapGet("/changes/{id:guid}", async (string id, HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.ChangeDetail(Actor(c), id, t)));
        api.MapPost("/changes", async (JsonElement body, HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.CreateChange(Actor(c), body, t)));
        api.MapPost("/changes/contacts", async (CreateContactRequest body, HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.ChangeContact(Actor(c), body, t)));
        foreach (var action in new[] { "teams", "tasks", "approvals", "updates", "tickets", "stakeholders", "attachments", "communications" })
        {
            var operation = action;
            api.MapPost("/changes/{id:guid}/" + operation, async (string id, JsonElement body, HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.ChangeAction(Actor(c), id, operation, null, body, t)));
        }
        api.MapPatch("/changes/{id:guid}/status", async (string id, JsonElement body, HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.ChangeAction(Actor(c), id, "status", null, body, t)));
        api.MapPatch("/changes/{id:guid}/tasks/{taskId:guid}", async (string id, string taskId, JsonElement body, HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.ChangeAction(Actor(c), id, "task-update", taskId, body, t)));
        api.MapPatch("/changes/{id:guid}/approvals/{approvalId:guid}", async (string id, string approvalId, JsonElement body, HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.ChangeAction(Actor(c), id, "decision", approvalId, body, t)));
        api.MapGet("/changes/{id:guid}/attachments/{attachmentId:guid}", async (string id, string attachmentId, HttpContext c, MySqlStore s, CancellationToken t) => { var file = await s.ChangeAttachment(Actor(c), id, attachmentId, t); return Results.File(file.Content, file.ContentType, file.FileName); });
    }
}

public sealed partial class MySqlStore
{
    private static readonly string[] ChangeStatuses = ["DRAFT", "SUBMITTED", "ASSESSMENT", "AWAITING_APPROVAL", "APPROVED", "SCHEDULED", "IN_PROGRESS", "VALIDATION", "COMPLETED", "CLOSED", "REJECTED", "CANCELLED", "FAILED", "ROLLED_BACK"];
    private static readonly string[] ChangeCategories = ["SERVER", "CLOUD", "MICROSOFT_365", "NETWORK", "SECURITY", "APPLICATION", "OTHER"];
    private static string JString(JsonElement body, string key, string fallback = "") => body.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;
    private static string? JOptional(JsonElement body, string key) => string.IsNullOrWhiteSpace(JString(body, key)) ? null : JString(body, key).Trim();
    private static bool JBool(JsonElement body, string key, bool fallback = false)
    {
        if (!body.TryGetProperty(key, out var value)) return fallback;
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean();
        throw new ArgumentException($"Choose true or false for {key}.");
    }
    private static int JInt(JsonElement body, string key, int fallback = 0)
    {
        if (!body.TryGetProperty(key, out var value)) return fallback;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        throw new ArgumentException($"Enter a whole number for {key}.");
    }
    private static JsonElement[] JArray(JsonElement body, string key) => body.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().ToArray() : [];
    private static string JChoice(JsonElement body, string key, params string[] allowed)
    {
        var v = JString(body, key);
        if (!allowed.Contains(v))
            throw new ArgumentException($"Choose a valid {key}.");
        return v;
    }
    private static DateTime? JDate(JsonElement body, string key)
    {
        var s = JOptional(body, key);
        if (s is null)
            return null;
        if (!DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var date))
            throw new ArgumentException($"Enter a valid {key}.");
        return date;
    }
    private async Task<(bool Admin, bool Manager, bool Coordinator)> ChangeActor(string actor, CancellationToken ct)
    {
        var admin = await IsAdministrator(actor, ct);
        var user = (await FQuery("SELECT change_access FROM users WHERE id=@id AND active=1 AND (access_expires_at IS NULL OR access_expires_at>UTC_TIMESTAMP(6))", ct, ("id", actor))).FirstOrDefault() ?? throw new UnauthorizedAccessException();
        var access = (string?)user["change_access"] ?? "NONE";
        if (!admin && access == "NONE")
            throw new UnauthorizedAccessException();
        return (admin, admin || access == "CHANGE_MANAGER", admin || access is "COORDINATOR" or "CHANGE_MANAGER");
    }
    private static string ChangeScope(string alias = "c") => FScope(alias) + $" AND (@manager=1 OR {alias}.requested_by_user_id=@actor OR {alias}.owner_user_id=@actor OR EXISTS(SELECT 1 FROM change_tasks ct WHERE ct.change_id={alias}.id AND ct.assigned_user_id=@actor) OR EXISTS(SELECT 1 FROM change_approvals ca WHERE ca.change_id={alias}.id AND (ca.approver_user_id=@actor OR EXISTS(SELECT 1 FROM department_users du WHERE du.user_id=@actor AND du.department_id=ca.department_id))) OR EXISTS(SELECT 1 FROM change_teams tm JOIN department_users du ON du.department_id=tm.department_id WHERE tm.change_id={alias}.id AND du.user_id=@actor))";
    private async Task<Dictionary<string, object?>> VisibleChange(string actor, string id, CancellationToken ct)
    {
        var access = await ChangeActor(actor, ct);
        return (await FQuery("SELECT c.* FROM changes c WHERE c.id=@id AND " + ChangeScope(), ct, ("id", id), ("actor", actor), ("admin", access.Admin), ("manager", access.Manager))).FirstOrDefault() ?? throw new KeyNotFoundException();
    }
    private static async Task ChangeEvent(MySqlConnection connection, MySqlTransaction tx, string actor, string id, string action, object details, CancellationToken ct)
    {
        await FExecute(connection, tx, "INSERT INTO change_history(change_id,actor_user_id,event,details) VALUES(@id,@actor,@event,CAST(@details AS JSON))", ct, ("id", id), ("actor", actor), ("event", action), ("details", FJson(details)));
        await AddAudit(connection, tx, actor, action, "CHANGE", id, details, ct);
    }
    public async Task<object> ChangeContact(string actor, CreateContactRequest body, CancellationToken ct)
    {
        await ChangeActor(actor, ct);
        return await CreateContact(actor, body, ct) ?? throw new UnauthorizedAccessException();
    }
    public async Task<object> ChangeOptions(string actor, CancellationToken ct)
    {
        await ChangeActor(actor, ct);
        var clients = await ListClients(actor, ct);
        var sites = await ListClientDirectory(actor, null, "sites", ct);
        var contacts = await ListClientDirectory(actor, null, "contacts", ct);
        var users = await FQuery("SELECT u.id,u.display_name AS name,GROUP_CONCAT(DISTINCT d.name ORDER BY d.name SEPARATOR ', ') AS departments,GROUP_CONCAT(DISTINCT du.expertise SEPARATOR '; ') AS expertise FROM users u LEFT JOIN department_users du ON du.user_id=u.id LEFT JOIN departments d ON d.id=du.department_id WHERE u.active=1 AND EXISTS(SELECT 1 FROM user_roles r WHERE r.user_id=u.id AND r.role_code IN ('INTERN','TECHNICIAN','TEAM_LEAD','MANAGER','ADMIN')) GROUP BY u.id,u.display_name ORDER BY u.display_name", ct);
        var departments = await ListDepartments(ct);
        var tickets = await ListTickets(actor, "all", null, "", 1, 100, ct);
        var mail = (await FQuery("SELECT enabled,change_address FROM mail_settings WHERE id=1", ct)).FirstOrDefault();
        return new
        {
            clients,
            sites,
            contacts,
            users,
            departments,
            tickets = tickets.Items,
            categories = ChangeCategories,
            types = new[] { "STANDARD", "NORMAL", "EMERGENCY" },
            risks = new[] { "LOW", "MEDIUM", "HIGH", "CRITICAL" },
            statuses = ChangeStatuses,
            internalClientId = "00000000-0000-4000-8000-000000000132",
            emailEnabled = mail is not null && FBool(mail["enabled"]),
            changeMailbox = mail?.GetValueOrDefault("change_address")
        };
    }
    public async Task<object> ChangeDashboard(string actor, CancellationToken ct)
    {
        var a = await ChangeActor(actor, ct);
        return (await FQuery("SELECT COUNT(*) AS total,COALESCE(SUM(c.status IN ('SUBMITTED','ASSESSMENT','AWAITING_APPROVAL')),0) AS awaiting_approval,COALESCE(SUM(c.status IN ('APPROVED','SCHEDULED')),0) AS scheduled,COALESCE(SUM(c.status='IN_PROGRESS'),0) AS in_progress,COALESCE(SUM(c.risk IN ('HIGH','CRITICAL') AND c.status NOT IN ('CLOSED','CANCELLED')),0) AS high_risk FROM changes c WHERE " + ChangeScope(), ct, ("actor", actor), ("admin", a.Admin), ("manager", a.Manager)))[0];
    }
    public async Task<object> ChangeCalendar(string actor, string? from, string? to, CancellationToken ct)
    {
        var a = await ChangeActor(actor, ct);
        var period = FPeriod(string.IsNullOrEmpty(from) ? DateTime.UtcNow.AddMonths(-1).ToString("yyyy-MM-dd") : from, string.IsNullOrEmpty(to) ? DateTime.UtcNow.AddMonths(2).ToString("yyyy-MM-dd") : to);
        return await FQuery("SELECT c.id,c.change_number,c.title,c.client_id,c.site_id,c.status,c.risk,c.planned_start,c.planned_end,cl.name AS client_name FROM changes c JOIN clients cl ON cl.id=c.client_id WHERE " + ChangeScope() + " AND c.planned_start<@end AND c.planned_end>=@start ORDER BY c.planned_start,c.id", ct, ("actor", actor), ("admin", a.Admin), ("manager", a.Manager), ("start", period.Start), ("end", period.End));
    }
    public async Task<object> ChangeList(string actor, IQueryCollection query, CancellationToken ct)
    {
        var a = await ChangeActor(actor, ct);
        var search = FText(query["q"], "Search", 0, 200);
        var status = string.IsNullOrEmpty(query["status"]) ? null : query["status"].ToString();
        if (status is not null && !ChangeStatuses.Contains(status))
            throw new ArgumentException("Choose a valid change status.");
        var client = string.IsNullOrEmpty(query["clientId"]) ? null : FGuid(query["clientId"], "client");
        var page = int.TryParse(query["page"], out var p) ? Math.Clamp(p, 1, 100000) : 1;
        var pageSize = int.TryParse(query["pageSize"], out var ps) ? Math.Clamp(ps, 1, 100) : 20;
        var args = new (string, object?)[] { ("actor", actor), ("admin", a.Admin), ("manager", a.Manager), ("search", search), ("pattern", "%" + search.ToLowerInvariant().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%"), ("status", status), ("client", client), ("take", pageSize), ("skip", (page - 1) * pageSize) };
        var filter = " FROM changes c JOIN clients cl ON cl.id=c.client_id JOIN users u ON u.id=c.owner_user_id WHERE " + ChangeScope() + " AND (@search='' OR LOWER(c.change_number) LIKE @pattern OR LOWER(c.title) LIKE @pattern) AND (@status IS NULL OR c.status=@status) AND (@client IS NULL OR c.client_id=@client)";
        var count = (await FQuery("SELECT COUNT(*) AS total" + filter, ct, args))[0]["total"];
        var items = await FQuery("SELECT c.*,cl.name AS client_name,u.display_name AS owner_name" + filter + " ORDER BY c.updated_at DESC,c.id LIMIT @take OFFSET @skip", ct, args);
        return new
        {
            total = count,
            items,
            page,
            pageSize
        };
    }
    public async Task<object> ChangeDetail(string actor, string id, CancellationToken ct)
    {
        var change = await VisibleChange(actor, id, ct);
        var args = new (string, object?)[] { ("id", id) };
        return new
        {
            change,
            teams = await FQuery("SELECT x.*,d.name AS department_name FROM change_teams x JOIN departments d ON d.id=x.department_id WHERE change_id=@id ORDER BY d.name", ct, args),
            tasks = await FQuery("SELECT x.*,d.name AS department_name,u.display_name AS assigned_name FROM change_tasks x LEFT JOIN departments d ON d.id=x.department_id LEFT JOIN users u ON u.id=x.assigned_user_id WHERE change_id=@id ORDER BY x.created_at", ct, args),
            approvals = await FQuery("SELECT x.*,u.display_name AS approver_name,d.name AS department_name,du.display_name AS decision_by_name FROM change_approvals x LEFT JOIN users u ON u.id=x.approver_user_id LEFT JOIN departments d ON d.id=x.department_id LEFT JOIN users du ON du.id=x.decision_by_user_id WHERE change_id=@id ORDER BY x.sequence,x.created_at", ct, args),
            updates = await FQuery("SELECT x.*,u.display_name AS author_name FROM change_updates x LEFT JOIN users u ON u.id=x.author_user_id WHERE change_id=@id ORDER BY x.created_at DESC", ct, args),
            links = await FQuery("SELECT l.*,t.ticket_number,t.subject FROM change_ticket_links l JOIN tickets t ON t.id=l.ticket_id WHERE change_id=@id", ct, args),
            history = await FQuery("SELECT h.*,u.display_name AS actor_name FROM change_history h LEFT JOIN users u ON u.id=h.actor_user_id WHERE change_id=@id ORDER BY h.id DESC", ct, args),
            communications = await FQuery("SELECT id,recipient_emails,subject,include_client,status,attempts,last_error_code,sent_at,created_at FROM change_communications WHERE change_id=@id ORDER BY created_at DESC", ct, args),
            stakeholders = await FQuery("SELECT s.id,s.contact_id,s.external_name,s.external_email,s.stakeholder_role,s.created_at,c.name AS contact_name,c.email AS contact_email,u.display_name AS legacy_user_name,u.email AS legacy_user_email FROM change_stakeholders s LEFT JOIN contacts c ON c.id=s.contact_id LEFT JOIN users u ON u.id=s.user_id WHERE change_id=@id ORDER BY s.created_at", ct, args),
            attachments = await FQuery("SELECT a.id,a.file_name,a.content_type,a.size_bytes,a.created_at,u.display_name AS uploaded_by FROM change_attachments a LEFT JOIN users u ON u.id=a.uploaded_by_user_id WHERE change_id=@id ORDER BY a.created_at DESC", ct, args)
        };
    }
    public async Task<object> CreateChange(string actor, JsonElement b, CancellationToken ct)
    {
        var access = await ChangeActor(actor, ct);
        var client = FGuid(JString(b, "clientId"), "client");
        var site = JOptional(b, "siteId");
        var department = JOptional(b, "departmentId");
        var owner = JOptional(b, "ownerUserId") ?? actor;
        var title = FText(JString(b, "title"), "Title", 3, 200);
        var description = FText(JString(b, "description"), "Description", 3, 10000);
        var category = JChoice(b, "category", ChangeCategories);
        var type = JChoice(b, "changeType", "STANDARD", "NORMAL", "EMERGENCY");
        var risk = JChoice(b, "risk", "LOW", "MEDIUM", "HIGH", "CRITICAL");
        var fields = new[] { ("impact", "Impact", 2, 5000), ("reason", "Reason", 2, 5000), ("businessJustification", "Business justification", 2, 5000), ("implementationPlan", "Implementation plan", 2, 10000), ("testPlan", "Test plan", 2, 10000), ("rollbackPlan", "Rollback plan", 2, 10000), ("communicationPlan", "Communication plan", 0, 10000) };
        foreach (var (key, label, min, max) in fields)
            FText(JString(b, key), label, min, max);
        var start = JDate(b, "plannedStart");
        var end = JDate(b, "plannedEnd");
        if ((start is null) != (end is null) || end <= start)
            throw new ArgumentException("Choose a valid planned start and end time.");
        var downtime = JInt(b, "downtimeMinutes");
        if (downtime < 0 || downtime > 525600)
            throw new ArgumentException("Downtime is outside the supported range.");
        if (JArray(b, "attachments").Length > 10 || JArray(b, "stakeholderContactIds").Length > 50 || JArray(b, "externalStakeholders").Length > 50)
            throw new ArgumentException("Too many attachments or stakeholders.");
        await using var c = CreateConnection();
        await c.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        if ((await FRows(c, tx, "SELECT t.id FROM clients t WHERE t.id=@client AND t.status='ACTIVE' AND (@admin=1 OR EXISTS(SELECT 1 FROM client_users cu WHERE cu.user_id=@actor AND cu.client_id=t.id)) FOR SHARE", ct, ("client", client), ("admin", access.Admin), ("actor", actor))).Count != 1)
            throw new UnauthorizedAccessException();
        if (site is not null && (await FRows(c, tx, "SELECT id FROM sites WHERE id=@site AND client_id=@client AND active=1 AND (@admin=1 OR NOT EXISTS(SELECT 1 FROM user_sites WHERE user_id=@actor) OR EXISTS(SELECT 1 FROM user_sites WHERE user_id=@actor AND site_id=@site)) FOR SHARE", ct, ("site", FGuid(site, "site")), ("client", client), ("admin", access.Admin), ("actor", actor))).Count != 1)
            throw new ArgumentException("Choose an accessible site belonging to this client.");
        if (department is not null && (await FRows(c, tx, "SELECT id FROM departments WHERE id=@department AND active=1 FOR SHARE", ct, ("department", FGuid(department, "department")))).Count == 0)
            throw new ArgumentException("Choose an active department.");
        if (site is null && !access.Admin && (await FRows(c, tx, "SELECT site_id FROM user_sites WHERE user_id=@actor LIMIT 1", ct, ("actor", actor))).Count > 0)
            throw new ArgumentException("Choose a site within your access scope.");
        await ValidateChangeStaff(c, tx, owner, client, ct, site);
        var number = (await FRows(c, tx, "SELECT next_value FROM astra_sequence_state WHERE name='change_number_seq' FOR UPDATE", ct)).FirstOrDefault() ?? throw new InvalidOperationException("Change numbering has not been initialized.");
        var next = Convert.ToInt64(number["next_value"]);
        await FExecute(c, tx, "UPDATE astra_sequence_state SET next_value=next_value+1 WHERE name='change_number_seq'", ct);
        var id = Guid.NewGuid().ToString("D");
        var reference = "CHG-" + next.ToString("D6");
        var args = new List<(string, object?)> { ("id", id), ("number", reference), ("client", client), ("site", site), ("department", department), ("title", title), ("description", description), ("category", category), ("type", type), ("risk", risk), ("outage", JBool(b, "outageExpected")), ("downtime", downtime), ("notify", JBool(b, "clientNotificationEnabled")), ("owner", owner), ("actor", actor), ("start", start), ("end", end) };
        args.AddRange(fields.Select(f => (f.Item1, (object?)JString(b, f.Item1).Trim())));
        await FExecute(c, tx, "INSERT INTO changes(id,change_number,client_id,site_id,department_id,title,description,category,change_type,risk,impact,outage_expected,downtime_minutes,reason,business_justification,implementation_plan,test_plan,rollback_plan,communication_plan,client_notification_enabled,owner_user_id,requested_by_user_id,planned_start,planned_end) VALUES(@id,@number,@client,@site,@department,@title,@description,@category,@type,@risk,@impact,@outage,@downtime,@reason,@businessJustification,@implementationPlan,@testPlan,@rollbackPlan,@communicationPlan,@notify,@owner,@actor,@start,@end)", ct, args.ToArray());
        var change = (await FRows(c, tx, "SELECT * FROM changes WHERE id=@id", ct, ("id", id)))[0];
        foreach (var contact in JArray(b, "stakeholderContactIds"))
            await AddChangeStakeholder(c, tx, actor, change, JsonSerializer.SerializeToElement(new
            {
                contactId = contact.GetString()
            }), ct);
        foreach (var external in JArray(b, "externalStakeholders"))
            await AddChangeStakeholder(c, tx, actor, change, JsonSerializer.SerializeToElement(new
            {
                externalName = JString(external, "name"),
                externalEmail = JString(external, "email"),
                role = JString(external, "role", "STAKEHOLDER")
            }), ct);
        foreach (var file in JArray(b, "attachments"))
            await SaveChangeAttachment(c, tx, actor, id, file, ct);
        await ChangeEvent(c, tx, actor, id, "CHANGE.CREATED", new
        {
            changeNumber = reference
        }, ct);
        await tx.CommitAsync(ct);
        return new
        {
            id,
            change_number = reference
        };
    }
    private static async Task ValidateChangeStaff(MySqlConnection c, MySqlTransaction tx, string user, string client, CancellationToken ct, string? site = null)
    {
        if ((await FRows(c, tx, "SELECT u.id FROM users u WHERE id=@user AND active=1 AND (access_expires_at IS NULL OR access_expires_at>UTC_TIMESTAMP(6)) AND EXISTS(SELECT 1 FROM user_roles ur WHERE ur.user_id=u.id AND ur.role_code IN ('INTERN','TECHNICIAN','TEAM_LEAD','MANAGER','ADMIN')) AND (EXISTS(SELECT 1 FROM user_roles r WHERE r.user_id=u.id AND r.role_code='ADMIN') OR EXISTS(SELECT 1 FROM client_users cu WHERE cu.user_id=u.id AND cu.client_id=@client)) AND (EXISTS(SELECT 1 FROM user_roles r WHERE r.user_id=u.id AND r.role_code='ADMIN') OR NOT EXISTS(SELECT 1 FROM user_sites us WHERE us.user_id=u.id) OR EXISTS(SELECT 1 FROM user_sites us WHERE us.user_id=u.id AND us.site_id=@site)) FOR SHARE", ct, ("user", FGuid(user, "staff member")), ("client", client), ("site", site))).Count == 0)
            throw new ArgumentException("Choose active staff with access to this client.");
    }
    private static async Task<object> AddChangeStakeholder(MySqlConnection c, MySqlTransaction tx, string actor, Dictionary<string, object?> change, JsonElement b, CancellationToken ct)
    {
        var contact = JOptional(b, "contactId");
        var email = JOptional(b, "externalEmail");
        var name = JOptional(b, "externalName");
        var role = JString(b, "role", "STAKEHOLDER");
        if (!new[] { "STAKEHOLDER", "BUSINESS_CONTACT", "TECHNICAL_CONTACT", "OBSERVER" }.Contains(role) || (contact is null) == (email is null))
            throw new ArgumentException("Choose a contact or enter an external stakeholder email.");
        if (contact is not null && (await FRows(c, tx, "SELECT ct.id FROM contacts ct WHERE ct.id=@contact AND ct.client_id=@client AND ct.active=1 AND (ct.site_id IS NULL OR ct.site_id=@site) AND NOT EXISTS(SELECT 1 FROM users u JOIN user_roles r ON r.user_id=u.id WHERE (u.id=ct.user_id OR LOWER(u.email)=LOWER(ct.email)) AND r.role_code IN ('INTERN','TECHNICIAN','TEAM_LEAD','MANAGER','ADMIN')) FOR SHARE", ct, ("contact", FGuid(contact, "contact")), ("client", change["client_id"]), ("site", change["site_id"]))).Count == 0)
            throw new ArgumentException("Choose an active customer contact for this change's client and site.");
        if (email is not null)
        {
            if (!System.Net.Mail.MailAddress.TryCreate(email, out var address) || address.Address != email || email.Length > 254)
                throw new ArgumentException("Enter a valid stakeholder email.");
            email = email.ToLowerInvariant();
        }
        var id = Guid.NewGuid().ToString("D");
        await FExecute(c, tx, "INSERT INTO change_stakeholders(id,change_id,contact_id,external_name,external_email,stakeholder_role,added_by_user_id) VALUES(@id,@change,@contact,@name,@email,@role,@actor)", ct, ("id", id), ("change", change["id"]), ("contact", contact), ("name", name), ("email", email), ("role", role), ("actor", actor));
        return new
        {
            id
        };
    }
    private static async Task<object> SaveChangeAttachment(MySqlConnection c, MySqlTransaction tx, string actor, string change, JsonElement b, CancellationToken ct)
    {
        var name = FText(JString(b, "fileName"), "File name", 1, 255);
        if (name.IndexOfAny(['/', '\\', '\r', '\n', '\0']) >= 0)
            throw new ArgumentException("Use a file name without directory paths.");
        var type = JString(b, "contentType");
        if (!new[] { "application/pdf", "application/zip", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "image/gif", "image/jpeg", "image/png", "text/csv", "text/plain" }.Contains(type))
            throw new ArgumentException("This attachment type is not supported.");
        var encoded = JString(b, "contentBase64");
        if (encoded.Length > 7 * 1024 * 1024)
            throw new ArgumentException("Attachments must be at most 5 MB.");
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(encoded);
        }
        catch (FormatException) { throw new ArgumentException("Attachment content is invalid."); }
        if (bytes.Length is < 1 or > 5 * 1024 * 1024)
            throw new ArgumentException("Attachments must be between 1 byte and 5 MB.");
        var id = Guid.NewGuid().ToString("D");
        await FExecute(c, tx, "INSERT INTO change_attachments(id,change_id,uploaded_by_user_id,file_name,content_type,size_bytes,sha256,content) VALUES(@id,@change,@actor,@name,@type,@size,@hash,@bytes)", ct, ("id", id), ("change", change), ("actor", actor), ("name", name), ("type", type), ("size", bytes.Length), ("hash", Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()), ("bytes", bytes));
        return new
        {
            id
        };
    }
    public async Task<TicketAttachmentFile> ChangeAttachment(string actor, string change, string id, CancellationToken ct)
    {
        await VisibleChange(actor, change, ct);
        var row = (await FQuery("SELECT file_name,content_type,content,sha256 FROM change_attachments WHERE id=@id AND change_id=@change", ct, ("id", id), ("change", change))).FirstOrDefault() ?? throw new KeyNotFoundException();
        var bytes = (byte[])row["content"]!;
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), (string)row["sha256"]!, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Attachment integrity verification failed.");
        return new((string)row["file_name"]!, (string)row["content_type"]!, bytes);
    }
}
