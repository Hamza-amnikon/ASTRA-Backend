using System.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Astra.Shared.Data;
using MySqlConnector;

namespace Astra.Shared.Services;

public sealed class AuthService(
    Database database,
    ISessionStore sessions,
    IdentityOptions options,
    IWebHostEnvironment environment,
    IConfiguration configuration)
{
    private static readonly Guid LocalAdminId = Guid.Parse("10000000-0000-4000-8000-000000000001");
    private static readonly Guid LocalUserId = Guid.Parse("10000000-0000-4000-8000-000000000002");

    public bool LocalAuthEnabled =>
        environment.IsDevelopment()
        && configuration.GetValue<bool>("ASTRA_LOCAL_AUTH_ENABLED")
        && !string.IsNullOrWhiteSpace(configuration["ASTRA_LOCAL_ADMIN_PASSWORD"])
        && !string.IsNullOrWhiteSpace(configuration["ASTRA_LOCAL_USER_PASSWORD"]);

    public async Task<(string Token, string CsrfToken)> LocalLoginAsync(string? username, string? password)
    {
        if (!LocalAuthEnabled)
            throw new ApiFault(404, "Local sign-in is disabled.");

        var account = (username ?? "").Trim().ToLowerInvariant();
        var expected = account switch
        {
            "admin" => configuration["ASTRA_LOCAL_ADMIN_PASSWORD"],
            "user" => configuration["ASTRA_LOCAL_USER_PASSWORD"],
            _ => null
        };

        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(password))
            throw new ApiFault(401, "Invalid local sign-in credentials.");

        var actualBytes = Encoding.UTF8.GetBytes(password);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        if (actualBytes.Length != expectedBytes.Length ||
            !CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes))
            throw new ApiFault(401, "Invalid local sign-in credentials.");

        var token = Token();
        var csrf = Token(32);
        var id = account == "admin" ? LocalAdminId : LocalUserId;
        await sessions.PutAsync(
            token,
            new SessionRecord(id, csrf),
            TimeSpan.FromSeconds(options.SessionSeconds));

        return (token, csrf);
    }

    public const string CookieName = "amnikon_session";
    public static string Token(int bytes = 48) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static Dictionary<string, object?> Params(Guid id) => new() { ["id"] = id };

    public async Task<SessionRecord> ReadSessionAsync(HttpContext context)
    {
        if (!context.Request.Cookies.TryGetValue(CookieName, out var token) || token.Length is < 20 or > 256) throw new ApiFault(401, "Authentication required.");
        return await sessions.ReadAsync(token) ?? throw new ApiFault(401, "Authentication required.");
    }

    public async Task<Actor> CurrentAsync(HttpContext context)
    {
        if (context.Items[typeof(Actor)] is Actor cached) return cached;
        var session = await ReadSessionAsync(context);

        // Development-only identities use real database scopes when MySQL is configured.
        if (LocalAuthEnabled && (session.UserId == LocalAdminId || session.UserId == LocalUserId))
        {
            var isAdmin = session.UserId == LocalAdminId;
            IReadOnlySet<string> localRoles = new HashSet<string>(
                [isAdmin ? "ADMIN" : "USER"], StringComparer.Ordinal);
            IReadOnlyList<Guid> localClients = Array.Empty<Guid>();
            IReadOnlyList<Guid> localSites = Array.Empty<Guid>();
            if (!string.IsNullOrWhiteSpace(configuration["MYSQL_CONNECTION_STRING"] ?? configuration.GetConnectionString("Astra")))
            {
                await using var localConnection = await database.OpenAsync(context.RequestAborted);
                var clientRows = await Database.QueryAsync(localConnection, isAdmin
                    ? "SELECT id FROM clients WHERE status='ACTIVE'"
                    : "SELECT client_id AS id FROM client_users WHERE user_id=@id", Params(session.UserId), cancellationToken: context.RequestAborted);
                localClients = clientRows.Select(row => (Guid)row["id"]!).ToArray();
                if (!isAdmin)
                    localSites = (await Database.QueryAsync(localConnection, "SELECT site_id AS id FROM user_sites WHERE user_id=@id", Params(session.UserId), cancellationToken: context.RequestAborted)).Select(row => (Guid)row["id"]!).ToArray();
            }
            var localAccount = new Actor(
                session.UserId,
                isAdmin ? "Local Admin" : "Local User",
                isAdmin ? "admin@local.test" : "user@local.test",
                localRoles,
                isAdmin ? new HashSet<string>(["*"], StringComparer.Ordinal)
                        : new HashSet<string>(StringComparer.Ordinal),
                localClients,
                localSites,
                "",
                isAdmin ? "CHANGE_MANAGER" : "NONE",
                isAdmin ? "ALL" : "CLIENT");

            var localRequested = context.Request.Headers["x-amnikon-workspace"].ToString();
            Actor localEffective;
            if (localRequested == "chooser" && context.Request.Path == "/auth/me")
                localEffective = localAccount;
            else
            {
                var workspace = localRequested.Length > 0 ? localRequested
                    : isAdmin ? "operations" : "client";
                localEffective = WorkspacePolicy.Select(localAccount, workspace);
            }

            context.Items[typeof(Actor)] = localEffective;
            return localEffective;
        }

        await using var connection = await database.OpenAsync(context.RequestAborted);
        var parameters = Params(session.UserId);
        var users = await Database.QueryAsync(connection, "SELECT id,display_name,email,active,session_version,work_scope,change_access FROM users WHERE id=@id", parameters, cancellationToken: context.RequestAborted);
        if (users.Count != 1 || !(bool)users[0]["active"]! || Convert.ToInt32(users[0]["session_version"]) != session.Version) throw new ApiFault(401, "Authentication required.");
        var row = users[0];
        async Task<HashSet<string>> Codes(string sql) => new((await Database.QueryAsync(connection, sql, parameters, cancellationToken: context.RequestAborted)).Select(r => (string)r["code"]!), StringComparer.Ordinal);
        async Task<Guid[]> Ids(string sql) => (await Database.QueryAsync(connection, sql, parameters, cancellationToken: context.RequestAborted)).Select(r => (Guid)r["id"]!).ToArray();
        var roles = await Codes("SELECT role_code AS code FROM user_roles WHERE user_id=@id");
        var groupPermissions = await Codes("SELECT DISTINCT p.`value` AS code FROM access_groups g JOIN staff_group_members gm ON gm.group_id=g.id JOIN JSON_TABLE(g.permissions, '$[*]' COLUMNS(`value` VARCHAR(100) PATH '$')) p ON TRUE WHERE gm.user_id=@id AND g.kind='STAFF' AND g.active=1");
        var clientIds = await Ids("SELECT client_id AS id FROM client_users WHERE user_id=@id");
        var siteIds = await Ids("SELECT site_id AS id FROM user_sites WHERE user_id=@id");
        var departmentIds = await Ids("SELECT department_id AS id FROM department_users WHERE user_id=@id");
        var admin = roles.Contains("ADMIN");
        var permissions = new HashSet<string>(roles.SelectMany(role => WorkspacePolicy.PermissionsFor(role)), StringComparer.Ordinal);
        var account = new Actor(session.UserId, (string)row["display_name"]!, (string)row["email"]!, roles, permissions, clientIds, siteIds, "", admin ? "CHANGE_MANAGER" : (string)row["change_access"]!, admin ? "ALL" : (string)row["work_scope"]!, groupPermissions, departmentIds);
        var requested = context.Request.Headers["x-amnikon-workspace"].ToString();
        Actor effective;
        if (requested == "chooser" && context.Request.Path == "/auth/me") effective = account;
        else
        {
            var workspace = requested.Length > 0 ? requested : admin ? "operations" : roles.Overlaps(["MANAGER", "TEAM_LEAD"]) ? "management" : roles.Overlaps(["INTERN", "TECHNICIAN"]) ? "operations" : "client";
            effective = WorkspacePolicy.Select(account, workspace);
            if (admin && workspace is "operations" or "management") effective = effective with { ClientIds = await Ids("SELECT id FROM clients"), SiteIds = [] };
        }
        context.Items[typeof(Actor)] = effective;
        return effective;
    }

    public async Task<Actor> RequirePermissionAsync(HttpContext context, string permission)
    {
        var actor = await CurrentAsync(context);
        if (!actor.Permissions.Contains("*") && !actor.Permissions.Contains(permission)) throw new ApiFault(403, "Permission required.");
        return actor;
    }

    public async Task VerifyCsrfAsync(HttpContext context)
    {
        if (context.Request.Headers.Origin.ToString() != options.Origin) throw new ApiFault(403, "Invalid request origin.");
        var session = await ReadSessionAsync(context);
        var actual = Encoding.UTF8.GetBytes(context.Request.Headers["x-csrf-token"].ToString());
        var expected = Encoding.UTF8.GetBytes(session.CsrfToken);
        if (actual.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(actual, expected)) throw new ApiFault(403, "Invalid request token.");
    }

    public async Task<object> ViewAsync(HttpContext context)
    {
        var actor = await CurrentAsync(context);
        var session = await ReadSessionAsync(context);
        var user = new { actor.Id, actor.DisplayName, actor.Email, actor.Roles, actor.ClientIds, actor.SiteIds, actor.Permissions, actor.GroupPermissions, actor.WorkScope, actor.ChangeAccess, DepartmentIds = actor.DepartmentIds ?? [], Workspace = actor.Workspace.Length > 0 ? actor.Workspace : null };
        return new { configured = true, authenticated = true, user, session.CsrfToken };
    }

    public async Task LogoutAsync(HttpContext context)
    {
        await CurrentAsync(context);
        await VerifyCsrfAsync(context);
        await sessions.DeleteAsync(context.Request.Cookies[CookieName]!);
        context.Response.Cookies.Delete(CookieName, CookieOptions());
    }

    public async Task<(string Token, string Destination)> LoginAsync(ClaimsPrincipal principal, string? returnTo, CancellationToken cancellationToken)
    {
        // Only call after the OIDC handler has validated signature, issuer, audience,
        // lifetime, nonce, correlation and PKCE. TicketReceived runs after validation.
        if (!Guid.TryParse(principal.FindFirstValue("tid"), out var tenant) || tenant.ToString() != options.TenantId?.ToLowerInvariant() || !Guid.TryParse(principal.FindFirstValue("oid"), out var objectId)) throw new ApiFault(401, "Invalid identity.");
        var email = principal.FindFirstValue("preferred_username") ?? principal.FindFirstValue("email");
        var name = principal.FindFirstValue("name");
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(name)) throw new ApiFault(403, "Identity is not available.");
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (MySqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var parameters = new Dictionary<string, object?> { ["tenant"] = tenant, ["object"] = objectId, ["email"] = email, ["name"] = name };
        var rows = await Database.QueryAsync(connection, "SELECT id,active,session_version FROM users WHERE entra_tenant_id=@tenant AND entra_object_id=@object FOR UPDATE", parameters, transaction, cancellationToken);
        Guid userId;
        int version;
        if (rows.Count == 1)
        {
            if (!(bool)rows[0]["active"]!) throw new ApiFault(403, "Account is not active.");
            userId = (Guid)rows[0]["id"]!;
            version = Convert.ToInt32(rows[0]["session_version"]);
            parameters["id"] = userId;
            await Database.ExecuteAsync(connection, "UPDATE users SET email=@email,display_name=@name,last_login_at=UTC_TIMESTAMP(6),updated_at=UTC_TIMESTAMP(6) WHERE id=@id", parameters, transaction, cancellationToken);
        }
        else
        {
            await Database.ExecuteAsync(connection, "SELECT name FROM app_locks WHERE name='global' FOR UPDATE", transaction: transaction, cancellationToken: cancellationToken);
            var count = await Database.QueryAsync(connection, "SELECT COUNT(*) AS n FROM users FOR UPDATE", transaction: transaction, cancellationToken: cancellationToken);
            if (Convert.ToInt64(count[0]["n"]) != 0 || !options.InitialAdministrators.Contains(objectId)) throw new ApiFault(403, "Account is not provisioned.");
            userId = Guid.NewGuid();
            version = 0;
            parameters["id"] = userId;
            await Database.ExecuteAsync(connection, "INSERT INTO users(id,entra_object_id,entra_tenant_id,email,display_name,last_login_at) VALUES(@id,@object,@tenant,@email,@name,UTC_TIMESTAMP(6)); INSERT INTO user_roles(user_id,role_code,granted_by) VALUES(@id,'ADMIN',@id); INSERT INTO audit_logs(actor_user_id,action,entity_type,entity_id,details) VALUES(@id,'IDENTITY.FIRST_ADMIN_PROVISIONED','USER',@id,JSON_OBJECT('tenantId',@tenant,'objectId',@object));", parameters, transaction, cancellationToken);
        }
        var roles = new HashSet<string>((await Database.QueryAsync(connection, "SELECT role_code FROM user_roles WHERE user_id=@id", parameters, transaction, cancellationToken)).Select(r => (string)r["role_code"]!), StringComparer.Ordinal);
        await transaction.CommitAsync(cancellationToken);
        var token = Token();
        await sessions.PutAsync(token, new SessionRecord(userId, Token(32), version), TimeSpan.FromSeconds(options.SessionSeconds));
        return (token, IdentityOptions.Destination(roles, returnTo));
    }

    public CookieOptions CookieOptions() => new() { Path = "/api", HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = options.BaseUri.Scheme == "https", MaxAge = TimeSpan.FromSeconds(options.SessionSeconds), IsEssential = true };
}
