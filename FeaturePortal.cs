using System.Text.Json;
using System.Security.Cryptography;
using MySqlConnector;

public static partial class FeatureApis
{
    private static void MapPortal(RouteGroupBuilder api)
    {
        api.MapGet("/portal/access/{id:guid}", async (string id, HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.PortalContactAccess(Actor(c), id, t)));
        api.MapPatch("/portal/access/{id:guid}", async (string id, JsonElement b, HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.LinkPortalContact(Actor(c), id, b, t)));
        api.MapGet("/portal/options", async (HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.PortalOptions(Actor(c), t)));
        api.MapGet("/portal/tickets", async (HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.PortalTickets(Actor(c), c.Request.Query["q"], c.Request.Query["page"], t)));
        api.MapGet("/portal/tickets/{id:guid}", async (string id, HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.PortalDetail(Actor(c), id, t)));
        api.MapPost("/portal/tickets", async (JsonElement b, HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.CreatePortalTicket(Actor(c), b, t)));
        api.MapPost("/portal/tickets/{id:guid}/replies", async (string id, JsonElement b, HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.ReplyPortalTicket(Actor(c), id, b, t)));
        api.MapGet("/portal/tickets/{ticket:guid}/attachments/{id:guid}", async (string ticket, string id, HttpContext c, MySqlStore s, CancellationToken t) => { var f = await s.PortalAttachment(Actor(c), ticket, id, t); return Results.File(f.Content, f.ContentType, f.FileName); });
    }
}
public sealed partial class MySqlStore
{
    private async Task RequirePortal(string actor, CancellationToken ct)
    {
        if ((await FQuery("SELECT u.id FROM users u JOIN user_roles r ON r.user_id=u.id WHERE u.id=@actor AND u.active=1 AND (u.access_expires_at IS NULL OR u.access_expires_at>UTC_TIMESTAMP(6)) AND r.role_code='USER'", ct, ("actor", actor))).Count == 0)
            throw new UnauthorizedAccessException();
    }
    private static string PortalGroup(string permission) => $"EXISTS(SELECT 1 FROM customer_group_members gm JOIN access_groups g ON g.id=gm.group_id JOIN contacts member ON member.id=gm.contact_id JOIN users member_user ON member_user.id=member.user_id WHERE member.user_id=@actor AND member.active=1 AND member_user.active=1 AND g.kind='CUSTOMER' AND g.active=1 AND gm.client_id=t.client_id AND g.client_id=t.client_id AND JSON_CONTAINS(g.permissions,JSON_QUOTE('{permission}')))";
    private static string PortalScope(bool reply = false) => $"(c.user_id=@actor OR {PortalGroup(reply ? "PORTAL_ORGANIZATION_REPLY" : "PORTAL_ORGANIZATION_VIEW")}) AND c.active=1 AND cl.status='ACTIVE' AND cl.organization_type='CLIENT' AND {FScope("t")}";
    private const string PortalFrom = " FROM tickets t JOIN contacts c ON c.id=t.requester_id JOIN clients cl ON cl.id=t.client_id LEFT JOIN sites s ON s.id=t.site_id LEFT JOIN categories ca ON ca.id=t.category_id";
    private async Task<Dictionary<string, object?>> PortalOwn(string actor, string id, bool reply, MySqlConnection connection, MySqlTransaction? tx, CancellationToken ct)
    {
        await RequirePortal(actor, ct);
        var row = (await FRows(connection, tx, $"SELECT t.id,t.ticket_number,t.subject,t.description,t.status,t.priority,t.created_at,t.updated_at,t.assigned_user_id,cl.name AS client_name,s.name AS site_name,ca.name AS category_name,(c.user_id=@actor OR {PortalGroup("PORTAL_ORGANIZATION_REPLY")}) AS can_reply" + PortalFrom + " WHERE t.id=@id AND " + PortalScope(reply) + (tx is null ? "" : " FOR UPDATE"), ct, ("id", id), ("actor", actor), ("admin", false))).FirstOrDefault() ?? throw new KeyNotFoundException();
        row["can_reply"] = FBool(row["can_reply"]);
        return row;
    }
    public async Task<object> PortalContactAccess(string actor, string id, CancellationToken ct)
    {
        await RequireAdmin(actor, ct);
        var contact = (await FQuery("SELECT id,client_id,user_id,email FROM contacts WHERE id=@id", ct, ("id", id))).FirstOrDefault() ?? throw new KeyNotFoundException();
        var users = await FQuery("SELECT u.id,u.display_name AS name,u.email FROM users u JOIN client_users cu ON cu.user_id=u.id WHERE cu.client_id=@client AND u.active=1 AND EXISTS(SELECT 1 FROM user_roles r WHERE r.user_id=u.id AND r.role_code='USER') ORDER BY u.display_name", ct, ("client", contact["client_id"]));
        return new
        {
            contact,
            users
        };
    }
    public async Task<object> LinkPortalContact(string actor, string id, JsonElement b, CancellationToken ct)
    {
        await RequireAdmin(actor, ct);
        var user = JOptional(b, "userId");
        if (user is not null)
            user = FGuid(user, "portal account");
        await using var c = CreateConnection();
        await c.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await FRows(c, tx, "SELECT id FROM users ORDER BY id FOR UPDATE", ct);
        var contact = (await FRows(c, tx, "SELECT * FROM contacts WHERE id=@id FOR UPDATE", ct, ("id", id))).FirstOrDefault() ?? throw new KeyNotFoundException();
        if (user is not null)
        {
            if ((await FRows(c, tx, "SELECT id FROM contacts WHERE user_id=@user AND id<>@id", ct, ("user", user), ("id", id))).Count > 0)
                throw new ArgumentException("This account is already linked to another contact.");
            if ((await FRows(c, tx, "SELECT u.id FROM users u JOIN client_users cu ON cu.user_id=u.id WHERE u.id=@user AND u.active=1 AND cu.client_id=@client AND LOWER(u.email)=LOWER(@email) AND EXISTS(SELECT 1 FROM user_roles r WHERE r.user_id=u.id AND r.role_code='USER')", ct, ("user", user), ("client", contact["client_id"]), ("email", contact["email"]))).Count == 0)
                throw new ArgumentException("Choose an active User-role account with the same email and client as this contact.");
        }
        await FExecute(c, tx, "UPDATE contacts SET user_id=@user,updated_at=UTC_TIMESTAMP(6) WHERE id=@id", ct, ("user", user), ("id", id));
        await AddAudit(c, tx, actor, "CONTACT.PORTAL_ACCESS_UPDATED", "CONTACT", id, new
        {
            beforeUserId = contact["user_id"],
            afterUserId = user
        }, ct);
        await tx.CommitAsync(ct);
        return new
        {
            saved = true
        };
    }
    public async Task<object> PortalOptions(string actor, CancellationToken ct)
    {
        await RequirePortal(actor, ct);
        var contacts = await FQuery("SELECT c.id,c.client_id,c.site_id,c.name,cl.name AS client_name,d.site_id AS routing_site_id FROM contacts c JOIN clients cl ON cl.id=c.client_id LEFT JOIN client_email_domains d ON d.domain=LOWER(SUBSTRING_INDEX(c.email,'@',-1)) AND d.active=1 AND d.client_id=c.client_id WHERE c.user_id=@actor AND c.active=1 AND cl.status='ACTIVE' AND cl.organization_type='CLIENT' AND EXISTS(SELECT 1 FROM client_users cu WHERE cu.user_id=@actor AND cu.client_id=c.client_id)", ct, ("actor", actor));
        var sites = await FQuery("SELECT s.id,s.client_id,s.name FROM sites s WHERE s.active=1 AND EXISTS(SELECT 1 FROM client_users cu WHERE cu.user_id=@actor AND cu.client_id=s.client_id) AND (NOT EXISTS(SELECT 1 FROM user_sites us WHERE us.user_id=@actor) OR EXISTS(SELECT 1 FROM user_sites us WHERE us.user_id=@actor AND us.site_id=s.id)) ORDER BY s.name", ct, ("actor", actor));
        return new
        {
            contacts,
            sites,
            categories = await ListCategories(ct)
        };
    }
    public async Task<object> PortalTickets(string actor, string? search, string? pageValue, CancellationToken ct)
    {
        await RequirePortal(actor, ct);
        var q = FText(search, "Search", 0, 200);
        var page = int.TryParse(pageValue, out var p) ? Math.Clamp(p, 1, 100000) : 1;
        var filter = PortalFrom + " WHERE " + PortalScope() + " AND (@q='' OR LOWER(t.subject) LIKE @pattern OR LOWER(t.ticket_number) LIKE @pattern)";
        var args = new (string, object?)[] { ("actor", actor), ("admin", false), ("q", q), ("pattern", "%" + q.ToLowerInvariant().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%"), ("skip", (page - 1) * 20) };
        var total = (await FQuery("SELECT COUNT(*) AS total" + filter, ct, args))[0]["total"];
        var items = await FQuery("SELECT t.id,t.ticket_number,t.subject,t.status,t.priority,t.updated_at,cl.name AS client_name" + filter + " ORDER BY t.updated_at DESC,t.id LIMIT 20 OFFSET @skip", ct, args);
        return new
        {
            total,
            items,
            page
        };
    }
    public async Task<object> PortalDetail(string actor, string id, CancellationToken ct)
    {
        await using var c = CreateConnection();
        await c.OpenAsync(ct);
        var ticket = await PortalOwn(actor, id, false, c, null, ct);
        ticket.Remove("assigned_user_id");
        var comments = await FQuery("SELECT c.id,c.body,c.created_at,u.display_name AS author_name FROM ticket_comments c JOIN users u ON u.id=c.author_user_id WHERE c.ticket_id=@id AND c.comment_type='PUBLIC' ORDER BY c.created_at,c.id", ct, ("id", id));
        var attachments = await FQuery("SELECT id,file_name,content_type,size_bytes,created_at FROM ticket_attachments WHERE ticket_id=@id AND visibility='PUBLIC' ORDER BY created_at,id", ct, ("id", id));
        var sla = (await FQuery("SELECT response_due_at,resolution_due_at,response_completed_at,resolution_completed_at,paused_at FROM ticket_sla WHERE ticket_id=@id", ct, ("id", id))).FirstOrDefault();
        return new
        {
            ticket,
            comments,
            attachments,
            sla
        };
    }
    private static async Task PortalFiles(MySqlConnection c, MySqlTransaction tx, string actor, string ticket, JsonElement body, CancellationToken ct)
    {
        var files = JArray(body, "attachments");
        if (files.Length > 5)
            throw new ArgumentException("Choose up to five attachments.");
        foreach (var file in files)
        {
            var name = FText(JString(file, "fileName"), "File name", 1, 180);
            var type = JString(file, "contentType");
            if (Path.GetFileName(name) != name || name.Contains('\\') || !new[] { "image/png", "image/jpeg", "image/gif", "image/webp", "application/pdf", "text/plain", "text/csv", "application/zip", "application/msword", "application/vnd.ms-excel", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" }.Contains(type))
                throw new ArgumentException("Choose an allowed attachment type.");
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(JString(file, "contentBase64"));
            }
            catch (FormatException) { throw new ArgumentException("Attachment data is invalid."); }
            if (bytes.Length is 0 or > 5 * 1024 * 1024)
                throw new ArgumentException("Each attachment must contain between 1 byte and 5 MB.");
            var id = Guid.NewGuid().ToString("D");
            await FExecute(c, tx, "INSERT INTO ticket_attachments(id,ticket_id,uploaded_by_user_id,file_name,content_type,size_bytes,storage_key,sha256,content,visibility) VALUES(@id,@ticket,@actor,@name,@type,@size,@key,@hash,@content,'PUBLIC')", ct, ("id", id), ("ticket", ticket), ("actor", actor), ("name", name), ("type", type), ("size", bytes.Length), ("key", ticket + "/" + id + "/" + name), ("hash", Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()), ("content", bytes));
        }
    }
    public async Task<object> CreatePortalTicket(string actor, JsonElement b, CancellationToken ct)
    {
        await RequirePortal(actor, ct);
        var contactId = FGuid(JString(b, "contactId"), "contact");
        var category = FGuid(JString(b, "categoryId"), "category");
        var subject = FText(JString(b, "subject"), "Subject", 3, 200);
        var description = FText(JString(b, "description"), "Description", 3, 20000);
        var site = JOptional(b, "siteId");
        if (site is not null)
            site = FGuid(site, "site");
        await using var c = CreateConnection();
        await c.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var contact = (await FRows(c, tx, "SELECT c.* FROM contacts c JOIN clients cl ON cl.id=c.client_id WHERE c.id=@id AND c.user_id=@actor AND c.active=1 AND cl.status='ACTIVE' AND cl.organization_type='CLIENT' AND EXISTS(SELECT 1 FROM client_users cu WHERE cu.user_id=@actor AND cu.client_id=c.client_id) FOR SHARE", ct, ("id", contactId), ("actor", actor))).FirstOrDefault() ?? throw new UnauthorizedAccessException();
        var client = (string)contact["client_id"]!;
        var routing = (await FRows(c, tx, "SELECT client_id,site_id FROM client_email_domains WHERE domain=LOWER(SUBSTRING_INDEX(@email,'@',-1)) AND active=1 FOR SHARE", ct, ("email", contact["email"]))).FirstOrDefault();
        if (routing is not null && !Equals(routing["client_id"], client))
            throw new ArgumentException("Your contact and email domain belong to different clients. Contact the service desk.");
        if (routing?["site_id"] is string forced && site is not null && site != forced)
            throw new ArgumentException("Use the site assigned to your email domain.");
        site = (string?)routing?.GetValueOrDefault("site_id") ?? site ?? (string?)contact["site_id"];
        if (contact["site_id"] is string contactSite && site != contactSite)
            throw new ArgumentException("Use the site linked to your contact.");
        if (site is not null && (await FRows(c, tx, "SELECT id FROM sites WHERE id=@id AND client_id=@client AND active=1 FOR SHARE", ct, ("id", site), ("client", client))).Count == 0)
            throw new ArgumentException("Choose a site belonging to your client.");
        if ((await FRows(c, tx, "SELECT site_id FROM user_sites WHERE user_id=@actor", ct, ("actor", actor))) is { Count: > 0 } assigned && !assigned.Any(x => Equals(x["site_id"], site)))
            throw new UnauthorizedAccessException();
        if ((await FRows(c, tx, "SELECT id FROM categories WHERE id=@id AND active=1 FOR SHARE", ct, ("id", category))).Count == 0)
            throw new ArgumentException("Choose an active category.");
        if ((await FRows(c, tx, "SELECT code FROM ticket_sources WHERE code='CLIENT_PORTAL' AND active=1 FOR SHARE", ct)).Count == 0)
            throw new ArgumentException("The client portal ticket source is disabled.");
        var id = Guid.NewGuid().ToString("D");
        var number = await AllocateTicketNumber(c, tx, client, site, ct);
        await FExecute(c, tx, "INSERT INTO tickets(id,ticket_number,client_id,requester_id,site_id,category_id,subject,description,source,priority,created_by_user_id) VALUES(@id,@number,@client,@contact,@site,@category,@subject,@description,'CLIENT_PORTAL','P3_MEDIUM',@actor)", ct, ("id", id), ("number", number), ("client", client), ("contact", contactId), ("site", site), ("category", category), ("subject", subject), ("description", description), ("actor", actor));
        await SetInitialTicketSla(c, tx, id, "P3_MEDIUM", ct);
        await PortalFiles(c, tx, actor, id, b, ct);
        await FExecute(c, tx, "INSERT INTO ticket_history(ticket_id,actor_user_id,action,new_value) VALUES(@id,@actor,'TICKET.CREATED',JSON_OBJECT('source','CLIENT_PORTAL'))", ct, ("id", id), ("actor", actor));
        await AddAudit(c, tx, actor, "TICKET.CREATED", "TICKET", id, new
        {
            source = "CLIENT_PORTAL"
        }, ct);
        await QueueTicketEvent(c, tx, actor, id, null, "Client portal ticket created", ct);
        await tx.CommitAsync(ct);
        return new
        {
            id,
            ticket_number = number
        };
    }
    public async Task<object> ReplyPortalTicket(string actor, string id, JsonElement b, CancellationToken ct)
    {
        var body = FText(JString(b, "body"), "Reply", 1, 20000);
        await using var c = CreateConnection();
        await c.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var ticket = await PortalOwn(actor, id, true, c, tx, ct);
        var comment = Guid.NewGuid().ToString("D");
        await FExecute(c, tx, "INSERT INTO ticket_comments(id,ticket_id,author_user_id,comment_type,body) VALUES(@comment,@id,@actor,'PUBLIC',@body)", ct, ("comment", comment), ("id", id), ("actor", actor), ("body", body));
        await PortalFiles(c, tx, actor, id, b, ct);
        await FExecute(c, tx, "UPDATE tickets SET updated_at=UTC_TIMESTAMP(6) WHERE id=@id", ct, ("id", id));
        await FExecute(c, tx, "INSERT INTO ticket_history(ticket_id,actor_user_id,action,new_value) VALUES(@id,@actor,'TICKET.CLIENT_REPLIED',JSON_OBJECT('commentId',@comment))", ct, ("id", id), ("actor", actor), ("comment", comment));
        await AddAudit(c, tx, actor, "TICKET.CLIENT_REPLIED", "TICKET", id, new
        {
            commentId = comment
        }, ct);
        if (ticket["assigned_user_id"] is string assigned)
            await InsertNotification(c, tx, assigned, id, "PUBLIC_REPLY", $"{ticket["ticket_number"]} received a client reply", (string)ticket["subject"]!, "portal-reply:" + comment, ct);
        await tx.CommitAsync(ct);
        return new
        {
            id = comment
        };
    }
    public async Task<TicketAttachmentFile> PortalAttachment(string actor, string ticket, string id, CancellationToken ct)
    {
        await using var c = CreateConnection();
        await c.OpenAsync(ct);
        await PortalOwn(actor, ticket, false, c, null, ct);
        var row = (await FRows(c, null, "SELECT file_name,content_type,content,storage_key,sha256 FROM ticket_attachments WHERE ticket_id=@ticket AND id=@id AND visibility='PUBLIC'", ct, ("ticket", ticket), ("id", id))).FirstOrDefault() ?? throw new KeyNotFoundException();
        var bytes = (byte[])row["content"]!;
        if (bytes.Length == 0)
        {
            var key = (string)row["storage_key"]!;
            if (Path.IsPathRooted(key) || key.Contains('\\') || key.Split('/').Any(p => p is "" or "." or ".."))
                throw new InvalidOperationException("The stored attachment path is invalid.");
            var root = Path.GetFullPath(configuration["ASTRA_PRIVATE_STORAGE_ROOT"] ?? Path.Combine(Directory.GetCurrentDirectory(), "storage"));
            var path = Path.GetFullPath(Path.Combine(root, key));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path))
                throw new KeyNotFoundException();
            bytes = await File.ReadAllBytesAsync(path, ct);
        }
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString((string)row["sha256"]!)))
            throw new InvalidOperationException("The stored attachment failed its integrity check.");
        return new((string)row["file_name"]!, (string)row["content_type"]!, bytes);
    }
}
