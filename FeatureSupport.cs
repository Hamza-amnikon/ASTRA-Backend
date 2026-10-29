using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MySqlConnector;

public static partial class FeatureApis
{
    private static bool SupportOrigin(HttpContext c)
    {
        var settings = c.RequestServices.GetRequiredService<AuthSettings>();
        var origin = c.Request.Headers.Origin.ToString();
        if (Uri.TryCreate(settings.ApplicationBaseUrl, UriKind.Absolute, out var expected) && origin == expected.GetLeftPart(UriPartial.Authority))
            return true;
        if (!c.RequestServices.GetRequiredService<IWebHostEnvironment>().IsDevelopment() || c.Connection.RemoteIpAddress is not { } address || !System.Net.IPAddress.IsLoopback(address))
            return false;
        var extra = c.RequestServices.GetRequiredService<IConfiguration>()["ASTRA_LOCAL_PREVIEW_ORIGINS"] ?? "";
        return extra.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Any(x => Uri.TryCreate(x, UriKind.Absolute, out var local) && local.IsLoopback && origin == local.GetLeftPart(UriPartial.Authority));
    }
    private static void MapSupport(WebApplication app)
    {
        var support = app.MapGroup("/api/support").AllowAnonymous().AddEndpointFilter<FeatureErrors>();
        support.AddEndpointFilter(async (invocation, next) => { var c = invocation.HttpContext; if (c.Request.Method == "POST" && !SupportOrigin(c)) return Error(403, "INVALID_ORIGIN", "Open the support page in the same browser before continuing."); return await next(invocation); });
        support.MapPost("/code", async (JsonElement body, HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.SupportCode(body, c.Connection.RemoteIpAddress?.ToString() ?? "unknown", t)));
        support.MapPost("/verify", async (JsonElement body, HttpContext c, MySqlStore s, CancellationToken t) =>
        {
            var token = await s.VerifySupportCode(body, c.Connection.RemoteIpAddress?.ToString() ?? "unknown", t);
            c.Response.Cookies.Append("astra_support_session", token, new CookieOptions { HttpOnly = true, Secure = c.Request.IsHttps, SameSite = SameSiteMode.Lax, Path = "/api/support", MaxAge = TimeSpan.FromHours(8) });
            return Results.Ok(new
            {
                verified = true
            });
        });
        support.MapGet("/options", async (HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.SupportOptions(c.Request.Cookies["astra_support_session"], t)));
        support.MapPost("/tickets", async (JsonElement body, HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.SubmitSupportTicket(c.Request.Cookies["astra_support_session"], c.Request.Headers["x-csrf-token"], body, t)));
        support.MapPost("/logout", async (HttpContext c, MySqlStore s, CancellationToken t) => { await s.SupportLogout(c.Request.Cookies["astra_support_session"], c.Request.Headers["x-csrf-token"], t); c.Response.Cookies.Delete("astra_support_session", new CookieOptions { Path = "/api/support" }); return Results.Ok(new { signedOut = true }); });
    }
}

public sealed partial class MySqlStore
{
    private static string SupportHash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string RandomToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    private string SupportCodeDigest(string challenge, string code) => Convert.ToHexString(HMACSHA256.HashData(MailConfigKey() ?? throw new FeatureFailure(503, "MAIL_KEY_MISSING", "Email verification encryption is not configured."), Encoding.UTF8.GetBytes("astra-support-v1:" + challenge + ":" + code))).ToLowerInvariant();
    private static string NormalizeDomain(string? domain)
    {
        domain = domain?.Trim().TrimEnd('.').ToLowerInvariant() ?? "";
        try
        {
            domain = new System.Globalization.IdnMapping().GetAscii(domain);
        }
        catch (ArgumentException) { throw new ArgumentException("Enter a valid client-owned domain."); }
        if (domain.Length > 253 || !domain.Contains('.') || domain.Split('.').Any(x => !System.Text.RegularExpressions.Regex.IsMatch(x, "^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$")))
            throw new ArgumentException("Enter an exact client domain without @, URLs or wildcards.");
        return domain;
    }
    private async Task SupportRate(string key, int maximum, int seconds, CancellationToken ct)
    {
        await using var c = CreateConnection();
        await c.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await FExecute(c, tx, "DELETE FROM astra_support_rates WHERE expires_at<UTC_TIMESTAMP(6) LIMIT 100", ct);
        await FExecute(c, tx, "INSERT INTO astra_support_rates(id,count,expires_at) VALUES(@id,1,TIMESTAMPADD(SECOND,@seconds,UTC_TIMESTAMP(6))) ON DUPLICATE KEY UPDATE count=IF(expires_at<=UTC_TIMESTAMP(6),1,LEAST(count+1,1000000000)),expires_at=IF(expires_at<=UTC_TIMESTAMP(6),TIMESTAMPADD(SECOND,@seconds,UTC_TIMESTAMP(6)),expires_at)", ct, ("id", SupportHash(key)), ("seconds", seconds));
        var count = FInt((await FRows(c, tx, "SELECT count FROM astra_support_rates WHERE id=@id FOR UPDATE", ct, ("id", SupportHash(key))))[0]["count"]);
        await tx.CommitAsync(ct);
        if (count > maximum)
            throw new FeatureFailure(429, "RATE_LIMITED", "Please wait before trying again.");
    }
    private async Task<Dictionary<string, object?>?> SupportRule(string domain, CancellationToken ct)
    {
        return (await FQuery("SELECT d.*,c.name AS client_name,COALESCE(s.ticket_prefix,c.ticket_prefix) AS ticket_prefix FROM client_email_domains d JOIN clients c ON c.id=d.client_id LEFT JOIN sites s ON s.id=d.site_id AND s.client_id=d.client_id WHERE d.domain=@domain AND d.active=1 AND c.status='ACTIVE' AND c.organization_type='CLIENT' AND (d.site_id IS NULL OR s.active=1)", ct, ("domain", domain))).FirstOrDefault();
    }
    public async Task<object> SupportCode(JsonElement b, string ip, CancellationToken ct)
    {
        var email = ValidEmail(JString(b, "email"), "work");
        var domain = NormalizeDomain(email.Split('@')[1]);
        email = email.Split('@')[0] + "@" + domain;
        await SupportRate("support-ip:" + ip, 30, 3600, ct);
        await SupportRate("global-delivery", 300, 3600, ct);
        await SupportRate("domain:" + domain, 60, 3600, ct);
        await SupportRate("email:" + email, 5, 3600, ct);
        await SupportRate("cooldown:" + email, 1, 60, ct);
        var settings = await MailSettingsRow(ct);
        if (!FBool(settings["enabled"]))
            throw new FeatureFailure(503, "EMAIL_DISABLED", "Email verification is unavailable until your service desk configures and enables outgoing email.");
        var connection = MailConnectionFor(settings);
        var rule = await SupportRule(domain, ct);
        var challenge = RandomToken();
        var code = RandomNumberGenerator.GetInt32(1000000).ToString("D6");
        await using (var c = CreateConnection())
        {
            await c.OpenAsync(ct);
            await using var tx = await c.BeginTransactionAsync(ct);
            await FExecute(c, tx, "DELETE FROM astra_support_challenges WHERE expires_at<UTC_TIMESTAMP(6) LIMIT 100", ct);
            await FExecute(c, tx, "DELETE FROM astra_support_sessions WHERE expires_at<UTC_TIMESTAMP(6) LIMIT 100", ct);
            await FExecute(c, tx, "INSERT INTO astra_support_challenges(id,email,domain_rule_id,revision,code_digest,expires_at) VALUES(@id,@email,@rule,@revision,@digest,UTC_TIMESTAMP(6)+INTERVAL 10 MINUTE)", ct, ("id", SupportHash(challenge)), ("email", email), ("rule", rule?.GetValueOrDefault("id")), ("revision", rule?.GetValueOrDefault("revision")), ("digest", SupportCodeDigest(challenge, code)));
            await tx.CommitAsync(ct);
        }
        if (rule is not null)
        {
            try
            {
                await MailTransport.SendAsync(connection, [email], "Your ASTRA support verification code", $"Your ASTRA support verification code is {code}.\n\nIt expires in 10 minutes and can be used once. Enter it only on the ASTRA support page. If you did not request it, ignore this email.", ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                await using var c = CreateConnection();
                await c.OpenAsync(ct);
                await using var tx = await c.BeginTransactionAsync(ct);
                await FExecute(c, tx, "DELETE FROM astra_support_challenges WHERE id=@id", ct, ("id", SupportHash(challenge)));
                await tx.CommitAsync(ct);
                throw new FeatureFailure(502, "EMAIL_DELIVERY_FAILED", "The verification email could not be sent. Contact your service desk or try again later.");
            }
        }
        return new
        {
            challenge,
            message = "If your work email is eligible for support, a verification code has been sent. Check your inbox and spam folder."
        };
    }
    public async Task<string> VerifySupportCode(JsonElement b, string ip, CancellationToken ct)
    {
        var challenge = JString(b, "challenge");
        var code = JString(b, "code");
        if (!System.Text.RegularExpressions.Regex.IsMatch(challenge, "^[a-f0-9]{64}$") || !System.Text.RegularExpressions.Regex.IsMatch(code, "^[0-9]{6}$"))
            throw new ArgumentException("Enter the six-digit verification code.");
        await SupportRate("verify-ip:" + ip, 60, 600, ct);
        await using var c = CreateConnection();
        await c.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var row = (await FRows(c, tx, "SELECT * FROM astra_support_challenges WHERE id=@id AND expires_at>UTC_TIMESTAMP(6) FOR UPDATE", ct, ("id", SupportHash(challenge)))).FirstOrDefault();
        if (row is null)
            throw new FeatureFailure(400, "CODE_INVALID", "This code is invalid or expired. Request a new code.");
        var attempts = FInt(row["attempts"]) + 1;
        var correct = attempts <= 5 && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes((string)row["code_digest"]!), Encoding.ASCII.GetBytes(SupportCodeDigest(challenge, code)));
        if (!correct)
        {
            if (attempts >= 5)
                await FExecute(c, tx, "DELETE FROM astra_support_challenges WHERE id=@id", ct, ("id", row["id"]));
            else
                await FExecute(c, tx, "UPDATE astra_support_challenges SET attempts=@attempts WHERE id=@id", ct, ("id", row["id"]), ("attempts", attempts));
            await tx.CommitAsync(ct);
            throw new FeatureFailure(400, "CODE_INVALID", "This code is invalid or expired. Request a new code if needed.");
        }
        var rule = (await FRows(c, tx, "SELECT d.* FROM client_email_domains d JOIN clients cl ON cl.id=d.client_id LEFT JOIN sites s ON s.id=d.site_id AND s.client_id=d.client_id WHERE d.id=@rule AND d.revision=@revision AND d.active=1 AND cl.status='ACTIVE' AND cl.organization_type='CLIENT' AND (d.site_id IS NULL OR s.active=1) FOR SHARE", ct, ("rule", row["domain_rule_id"]), ("revision", row["revision"]))).FirstOrDefault();
        await FExecute(c, tx, "DELETE FROM astra_support_challenges WHERE id=@id", ct, ("id", row["id"]));
        if (rule is null)
        {
            await tx.CommitAsync(ct);
            throw new FeatureFailure(400, "CODE_INVALID", "This code is invalid or expired. Request a new code.");
        }
        var token = RandomToken();
        await FExecute(c, tx, "INSERT INTO astra_support_sessions(id,email,domain_rule_id,client_id,revision,csrf_token,expires_at) VALUES(@id,@email,@rule,@client,@revision,@csrf,UTC_TIMESTAMP(6)+INTERVAL 8 HOUR)", ct, ("id", SupportHash(token)), ("email", row["email"]), ("rule", rule["id"]), ("client", rule["client_id"]), ("revision", rule["revision"]), ("csrf", RandomToken()));
        await tx.CommitAsync(ct);
        return token;
    }
    private async Task<Dictionary<string, object?>> SupportIdentity(string? token, string? csrf, CancellationToken ct)
    {
        if (token is null || !System.Text.RegularExpressions.Regex.IsMatch(token, "^[a-f0-9]{64}$"))
            throw new FeatureFailure(401, "SUPPORT_SESSION_EXPIRED", "Verify your work email to continue.");
        var row = (await FQuery("SELECT x.*,d.site_id,c.name AS client_name,COALESCE(s.ticket_prefix,c.ticket_prefix) AS ticket_prefix FROM astra_support_sessions x JOIN client_email_domains d ON d.id=x.domain_rule_id AND d.client_id=x.client_id AND d.revision=x.revision AND d.active=1 JOIN clients c ON c.id=x.client_id AND c.status='ACTIVE' AND c.organization_type='CLIENT' LEFT JOIN sites s ON s.id=d.site_id AND s.client_id=d.client_id WHERE x.id=@id AND x.expires_at>UTC_TIMESTAMP(6) AND (d.site_id IS NULL OR s.active=1)", ct, ("id", SupportHash(token)))).FirstOrDefault() ?? throw new FeatureFailure(401, "SUPPORT_SESSION_EXPIRED", "Your support access expired or changed. Verify your email again.");
        if (csrf is not null)
        {
            var expected = (string)row["csrf_token"]!;
            if (csrf.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(csrf), Encoding.UTF8.GetBytes(expected)))
                throw new FeatureFailure(403, "INVALID_CSRF", "Refresh the support page before submitting.");
        }
        return row;
    }
    public async Task<object> SupportOptions(string? token, CancellationToken ct)
    {
        var identity = await SupportIdentity(token, null, ct);
        return new
        {
            email = identity["email"],
            csrfToken = identity["csrf_token"],
            client = new
            {
                name = identity["client_name"],
                ticketPrefix = identity["ticket_prefix"]
            },
            fixedSiteId = identity["site_id"],
            categories = await ListCategories(ct),
            sites = await FQuery("SELECT id,name,ticket_prefix FROM sites WHERE client_id=@client AND active=1 AND (@site IS NULL OR id=@site) ORDER BY name", ct, ("client", identity["client_id"]), ("site", identity["site_id"]))
        };
    }
    public async Task<object> SubmitSupportTicket(string? token, string? csrf, JsonElement b, CancellationToken ct)
    {
        var identity = await SupportIdentity(token, csrf ?? "", ct);
        await SupportRate("submit:" + identity["email"], 10, 3600, ct);
        var request = FGuid(JString(b, "requestId"), "request identifier");
        var name = FText(JString(b, "name"), "Name", 2, 120);
        var subject = FText(JString(b, "subject"), "Subject", 3, 200);
        var description = FText(JString(b, "description"), "Description", 3, 10000);
        var category = FGuid(JString(b, "categoryId"), "category");
        var requestedSite = JOptional(b, "siteId");
        await using var c = CreateConnection();
        await c.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        // A customer row lock serializes contact creation, request retries and ticket numbering.
        if ((await FRows(c, tx, "SELECT id FROM clients WHERE id=@id AND status='ACTIVE' AND organization_type='CLIENT' FOR UPDATE", ct, ("id", identity["client_id"]))).Count == 0)
            throw new FeatureFailure(401, "SUPPORT_SESSION_EXPIRED", "Your support access changed. Verify your email again.");
        if ((await FRows(c, tx, "SELECT id FROM client_email_domains WHERE id=@id AND client_id=@client AND revision=@revision AND active=1 FOR SHARE", ct, ("id", identity["domain_rule_id"]), ("client", identity["client_id"]), ("revision", identity["revision"]))).Count == 0)
            throw new FeatureFailure(401, "SUPPORT_SESSION_EXPIRED", "Your support access changed. Verify your email again.");
        var previous = (await FRows(c, tx, "SELECT p.requester_email,p.client_id,t.id,t.ticket_number FROM portal_ticket_submissions p JOIN tickets t ON t.id=p.ticket_id WHERE request_id=@request", ct, ("request", request))).FirstOrDefault();
        if (previous is not null)
        {
            if (!Equals(previous["requester_email"], identity["email"]) || !Equals(previous["client_id"], identity["client_id"]))
                throw new UnauthorizedAccessException();
            await tx.CommitAsync(ct);
            return new
            {
                id = previous["id"],
                ticket_number = previous["ticket_number"]
            };
        }
        var contacts = await FRows(c, tx, "SELECT id,site_id,active FROM contacts WHERE client_id=@client AND LOWER(email)=@email ORDER BY id FOR UPDATE", ct, ("client", identity["client_id"]), ("email", identity["email"]));
        if (contacts.Count > 1)
            throw new ArgumentException("Your service desk must review duplicate contact records before you submit.");
        var contact = contacts.FirstOrDefault();
        if (contact is not null && !FBool(contact["active"]))
            throw new ArgumentException("Your contact record is disabled. Contact your service desk.");
        var site = (string?)identity["site_id"] ?? (string?)contact?.GetValueOrDefault("site_id") ?? requestedSite;
        if ((identity["site_id"] is not null && requestedSite is not null && !Equals(identity["site_id"], requestedSite)) || (contact?.GetValueOrDefault("site_id") is string assignedSite && site != assignedSite) || (requestedSite is not null && requestedSite != site))
            throw new ArgumentException("The selected site does not match your support access.");
        if (site is not null && (await FRows(c, tx, "SELECT id FROM sites WHERE id=@id AND client_id=@client AND active=1 FOR SHARE", ct, ("id", FGuid(site, "site")), ("client", identity["client_id"]))).Count == 0)
            throw new ArgumentException("Choose an active site belonging to your client.");
        if ((await FRows(c, tx, "SELECT id FROM categories WHERE id=@id AND active=1 FOR SHARE", ct, ("id", category))).Count == 0)
            throw new ArgumentException("Choose an active category.");
        if ((await FRows(c, tx, "SELECT code FROM ticket_sources WHERE code='CLIENT_PORTAL' AND active=1 FOR SHARE", ct)).Count == 0)
            throw new FeatureFailure(503, "PORTAL_SOURCE_DISABLED", "Your service desk must enable the client portal ticket source.");
        var requester = (string?)contact?.GetValueOrDefault("id") ?? Guid.NewGuid().ToString("D");
        if (contact is null)
            await FExecute(c, tx, "INSERT INTO contacts(id,client_id,site_id,name,email) VALUES(@id,@client,@site,@name,@email)", ct, ("id", requester), ("client", identity["client_id"]), ("site", site), ("name", name), ("email", identity["email"]));
        var ticket = Guid.NewGuid().ToString("D");
        var number = await AllocateTicketNumber(c, tx, (string)identity["client_id"]!, site, ct);
        await FExecute(c, tx, "INSERT INTO tickets(id,ticket_number,client_id,requester_id,site_id,category_id,subject,description,source,priority) VALUES(@id,@number,@client,@requester,@site,@category,@subject,@description,'CLIENT_PORTAL','P3_MEDIUM')", ct, ("id", ticket), ("number", number), ("client", identity["client_id"]), ("requester", requester), ("site", site), ("category", category), ("subject", subject), ("description", description));
        await SetInitialTicketSla(c, tx, ticket, "P3_MEDIUM", ct);
        await FExecute(c, tx, "INSERT INTO portal_ticket_submissions(request_id,domain_rule_id,client_id,requester_email,ticket_id) VALUES(@request,@rule,@client,@email,@ticket)", ct, ("request", request), ("rule", identity["domain_rule_id"]), ("client", identity["client_id"]), ("email", identity["email"]), ("ticket", ticket));
        await FExecute(c, tx, "INSERT INTO ticket_history(ticket_id,action,new_value) VALUES(@id,'TICKET.CREATED',JSON_OBJECT('source','CLIENT_PORTAL','verifiedEmail',@email))", ct, ("id", ticket), ("email", identity["email"]));
        await FExecute(c, tx, "INSERT INTO audit_logs(action,entity_type,entity_id,details) VALUES('PORTAL.TICKET_SUBMITTED','TICKET',@id,JSON_OBJECT('verifiedEmail',@email,'clientId',@client,'domainRuleId',@rule,'identityMethod','EMAIL_CODE'))", ct, ("id", ticket), ("email", identity["email"]), ("client", identity["client_id"]), ("rule", identity["domain_rule_id"]));
        await QueueTicketEvent(c, tx, null, ticket, null, "Verified support ticket created", ct);
        await tx.CommitAsync(ct);
        return new
        {
            id = ticket,
            ticket_number = number
        };
    }
    private static async Task SetInitialTicketSla(MySqlConnection c, MySqlTransaction tx, string ticket, string priority, CancellationToken ct)
    {
        var calendar = await GetSlaCalendar(c, tx, priority, ct);
        if (calendar is null)
            return;
        var now = DateTime.UtcNow;
        await FExecute(c, tx, "INSERT INTO ticket_sla(ticket_id,policy_id,calendar_id,target_priority,response_warning_at,response_due_at,resolution_warning_at,resolution_due_at) VALUES(@ticket,@policy,@calendar,@priority,@rw,@rd,@sw,@sd)", ct, ("ticket", ticket), ("policy", calendar.PolicyId), ("calendar", calendar.Id), ("priority", priority), ("rw", SlaBusinessCalendar.Add(calendar, now, Math.Max(1, calendar.ResponseMinutes * calendar.WarningPercent / 100))), ("rd", SlaBusinessCalendar.Add(calendar, now, calendar.ResponseMinutes)), ("sw", SlaBusinessCalendar.Add(calendar, now, Math.Max(1, calendar.ResolutionMinutes * calendar.WarningPercent / 100))), ("sd", SlaBusinessCalendar.Add(calendar, now, calendar.ResolutionMinutes)));
    }
    public async Task SupportLogout(string? token, string? csrf, CancellationToken ct)
    {
        await SupportIdentity(token, csrf ?? "", ct);
        await using var c = CreateConnection();
        await c.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await FExecute(c, tx, "DELETE FROM astra_support_sessions WHERE id=@id", ct, ("id", SupportHash(token!)));
        await tx.CommitAsync(ct);
    }
}
