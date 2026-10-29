using System.Text.Json;

public static partial class FeatureApis
{
    private static void MapDomains(RouteGroupBuilder api)
    {
        api.MapGet("/admin/client-domains", async (HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.ClientDomains(Actor(c), c.Request.Query["siteId"], t)));
        api.MapPost("/admin/client-domains", async (JsonElement body, HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.SaveClientDomain(Actor(c), null, body, t)));
        api.MapPatch("/admin/client-domains/{id:guid}", async (string id, JsonElement body, HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.SaveClientDomain(Actor(c), id, body, t)));
    }
}
public sealed partial class MySqlStore
{
    public async Task<object> ClientDomains(string actor, string? siteId, CancellationToken ct)
    {
        await RequireAdmin(actor, ct);
        siteId = string.IsNullOrEmpty(siteId) ? null : FGuid(siteId, "site");
        var domains = await FQuery("SELECT d.*,c.name AS client_name,COALESCE(s.ticket_prefix,c.ticket_prefix) AS ticket_prefix,s.name AS site_name FROM client_email_domains d JOIN clients c ON c.id=d.client_id LEFT JOIN sites s ON s.id=d.site_id WHERE @site IS NULL OR d.site_id=@site ORDER BY c.name,d.domain", ct, ("site", siteId));
        var clients = await FQuery("SELECT id,name,ticket_prefix FROM clients WHERE status='ACTIVE' AND organization_type='CLIENT' ORDER BY name", ct);
        var sites = await FQuery("SELECT id,name,client_id FROM sites WHERE active=1 ORDER BY name", ct);
        var mail = await MailSettingsRow(ct);
        return new
        {
            domains,
            clients,
            sites,
            emailEnabled = FBool(mail["enabled"]),
            emailConfigured = mail["password_ciphertext"] is string { Length: > 0 } && mail["smtp_username"] is string { Length: > 0 } && MailConfigKey() is not null
        };
    }
    public async Task<object> SaveClientDomain(string actor, string? id, JsonElement b, CancellationToken ct)
    {
        await RequireAdmin(actor, ct);
        var client = FGuid(JString(b, "clientId"), "client");
        var site = JOptional(b, "siteId");
        if (site is not null)
            FGuid(site, "site");
        var domain = NormalizeDomain(JString(b, "domain"));
        if (new[] { "gmail.com", "googlemail.com", "outlook.com", "hotmail.com", "live.com", "yahoo.com", "yahoo.co.in", "icloud.com", "aol.com", "proton.me", "protonmail.com", "mail.com", "gmx.com" }.Contains(domain))
            throw new ArgumentException("Shared personal email domains cannot identify a client. Use a client-owned work domain.");
        await using var c = CreateConnection();
        await c.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        if ((await FRows(c, tx, "SELECT id FROM clients WHERE id=@client AND status='ACTIVE' AND organization_type='CLIENT' FOR SHARE", ct, ("client", client))).Count == 0)
            throw new ArgumentException("Choose an active customer organization.");
        if (site is not null && (await FRows(c, tx, "SELECT id FROM sites WHERE id=@id AND client_id=@client AND active=1 FOR SHARE", ct, ("id", site), ("client", client))).Count == 0)
            throw new ArgumentException("Choose a site belonging to this client.");
        Dictionary<string, object?>? before = null;
        if (id is not null)
        {
            before = (await FRows(c, tx, "SELECT * FROM client_email_domains WHERE id=@id FOR UPDATE", ct, ("id", id))).FirstOrDefault() ?? throw new KeyNotFoundException();
            if (!Equals(before["client_id"], client))
                throw new ArgumentException("A domain rule cannot be moved to another client. Disable it and review the client transfer first.");
            if (JInt(b, "revision") != FInt(before["revision"]))
                throw new InvalidOperationException("Domain routing changed. Reload before saving.");
        }
        var entity = id ?? Guid.NewGuid().ToString("D");
        await FExecute(c, tx, id is null ? "INSERT INTO client_email_domains(id,client_id,domain,site_id,active) VALUES(@id,@client,@domain,@site,@active)" : "UPDATE client_email_domains SET domain=@domain,site_id=@site,active=@active,revision=revision+1,updated_at=UTC_TIMESTAMP(6) WHERE id=@id", ct, ("id", entity), ("client", client), ("domain", domain), ("site", site), ("active", JBool(b, "active")));
        var next = (await FRows(c, tx, "SELECT * FROM client_email_domains WHERE id=@id", ct, ("id", entity)))[0];
        await AddAudit(c, tx, actor, "CLIENT.PORTAL_DOMAIN_SAVED", "CLIENT_EMAIL_DOMAIN", entity, new
        {
            previous = before,
            next
        }, ct);
        await tx.CommitAsync(ct);
        return next;
    }
}
