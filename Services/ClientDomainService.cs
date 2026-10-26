using System.Globalization;
using System.Text.RegularExpressions;
using Astra.Api.Data;
using Astra.Api.Security;
using Microsoft.Data.SqlClient;

namespace Astra.Api.Services;

public sealed partial class ClientDomainService(Database database)
{
    private static readonly HashSet<string> PersonalProviders = new(["gmail.com", "googlemail.com", "outlook.com", "hotmail.com", "live.com", "yahoo.com", "icloud.com", "aol.com", "proton.me", "protonmail.com", "mail.com"], StringComparer.Ordinal);
    [GeneratedRegex(@"[\s/\\:@?#%*]", RegexOptions.CultureInvariant)] private static partial Regex ForbiddenDomainCharacters();
    [GeneratedRegex(@"^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.CultureInvariant)] private static partial Regex LabelPattern();
    public static string Normalize(string domain)
    {
        if (domain is null || domain.Length > 253 || ForbiddenDomainCharacters().IsMatch(domain.Trim())) throw new ApiFault(400, "Enter a domain without URLs or wildcards.");
        string ascii;
        try { ascii = new IdnMapping().GetAscii(domain.Trim().ToLowerInvariant()).ToLowerInvariant(); }
        catch (ArgumentException) { throw new ApiFault(400, "Enter a valid work domain."); }
        if (ascii.Length > 253 || !ascii.Contains('.', StringComparison.Ordinal) || ascii.Split('.').Any(label => !LabelPattern().IsMatch(label))) throw new ApiFault(400, "Enter a domain such as example.com, without @, URLs or wildcards.");
        return ascii;
    }
    public static string NormalizeEmail(string value)
    {
        var email = value.Trim().ToLowerInvariant(); var at = email.LastIndexOf('@');
        if (at < 1 || email.Length > 254 || email[..at].Any(c => char.IsWhiteSpace(c) || c == '@')) throw new ApiFault(400, "Enter your work email address.");
        return email[..at] + "@" + Normalize(email[(at + 1)..]);
    }
    public async Task<object> ListAsync(Guid? siteId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        var domains = await Database.QueryAsync(connection, "SELECT " + string.Join(',', ColumnCatalog.Columns("client_email_domains").Select(c => "d.[" + c + "]")) + ",c.name AS client_name,COALESCE(s.ticket_prefix,c.ticket_prefix) AS ticket_prefix,s.name AS site_name FROM dbo.client_email_domains d JOIN dbo.clients c ON c.id=d.client_id LEFT JOIN dbo.sites s ON s.id=d.site_id WHERE(@site IS NULL OR d.site_id=@site) ORDER BY c.name,d.domain", new Dictionary<string, object?> { ["site"] = siteId }, cancellationToken: cancellationToken);
        var clients = await Database.QueryAsync(connection, "SELECT id,name,ticket_prefix FROM dbo.clients WHERE status='ACTIVE' AND organization_type='CLIENT' ORDER BY name", cancellationToken: cancellationToken);
        var sites = await Database.QueryAsync(connection, "SELECT id,name,client_id FROM dbo.sites WHERE active=1 ORDER BY name", cancellationToken: cancellationToken);
        var mail = await Database.QueryAsync(connection, "SELECT enabled,CONVERT(bit,CASE WHEN smtp_username IS NOT NULL AND password_ciphertext IS NOT NULL THEN 1 ELSE 0 END) AS configured FROM dbo.mail_settings WHERE id=1", cancellationToken: cancellationToken);
        return new { domains, clients, sites, emailEnabled = mail.Count > 0 && (bool)mail[0]["enabled"]!, emailConfigured = mail.Count > 0 && (bool)mail[0]["configured"]!, portalPath = "/support" };
    }
    public async Task<object> SaveAsync(Actor actor, DomainInput input, Guid? id, CancellationToken cancellationToken)
    {
        var domain = Normalize(input.Domain);
        if (input.Revision is < 1) throw new ApiFault(400, "Invalid revision.");
        if (PersonalProviders.Contains(domain)) throw new ApiFault(400, "Shared personal email provider domains cannot identify a client. Use a client-owned work domain.");
        await using var connection = await database.OpenAsync(cancellationToken); await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var parameters = new Dictionary<string, object?> { ["id"] = id, ["client"] = input.ClientId, ["domain"] = domain, ["site"] = input.SiteId, ["active"] = input.Active };
        if ((await Database.QueryAsync(connection, "SELECT id FROM dbo.clients WITH(HOLDLOCK) WHERE id=@client AND status='ACTIVE' AND organization_type='CLIENT'", parameters, transaction, cancellationToken)).Count == 0) throw new ApiFault(400, "Choose an active client organization.");
        if (input.SiteId.HasValue && (await Database.QueryAsync(connection, "SELECT id FROM dbo.sites WITH(HOLDLOCK) WHERE id=@site AND client_id=@client AND active=1", parameters, transaction, cancellationToken)).Count == 0) throw new ApiFault(400, "Choose a site belonging to this client.");
        var columns = string.Join(',', ColumnCatalog.Columns("client_email_domains").Select(c => "[" + c + "]"));
        var prior = id.HasValue ? await Database.QueryAsync(connection, "SELECT " + columns + " FROM dbo.client_email_domains WITH(UPDLOCK,HOLDLOCK) WHERE id=@id", parameters, transaction, cancellationToken) : [];
        if (id.HasValue && prior.Count == 0) throw new ApiFault(400, "Domain rule not found.");
        var before = prior.Count > 0 ? prior[0] : null;
        if (before is not null)
        {
            if (input.Revision.HasValue && input.Revision.Value != Convert.ToInt32(before["revision"])) throw new ApiFault(400, "This domain routing changed. Reload it before saving.");
            if ((Guid)before["client_id"]! != input.ClientId) throw new ApiFault(400, "A domain rule cannot be moved to another client. Disable this rule and contact the platform administrator to review a client transfer.");
        }
        parameters["id"] ??= Guid.NewGuid();
        await Database.ExecuteAsync(connection, id.HasValue ? "UPDATE dbo.client_email_domains SET domain=@domain,site_id=@site,active=@active,revision=revision+1,updated_at=SYSUTCDATETIME() WHERE id=@id" : "INSERT dbo.client_email_domains(id,client_id,domain,site_id,active) VALUES(@id,@client,@domain,@site,@active)", parameters, transaction, cancellationToken);
        var row = (await Database.QueryAsync(connection, "SELECT " + columns + " FROM dbo.client_email_domains WHERE id=@id", parameters, transaction, cancellationToken)).Single();
        await Audit.WriteAsync(connection, transaction, actor.Id, "CLIENT.PORTAL_DOMAIN_SAVED", "CLIENT_EMAIL_DOMAIN", (Guid)parameters["id"]!, new { previous = before, next = row }, cancellationToken);
        await transaction.CommitAsync(cancellationToken); return row;
    }
    private static async Task<Actor> Administrator(HttpContext context, AuthService auth)
    { var actor = await auth.CurrentAsync(context); if (!actor.Roles.Contains("ADMIN")) throw new ApiFault(403, "Administration access required."); return actor; }

}
