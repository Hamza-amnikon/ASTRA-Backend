using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using MySqlConnector;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["ASPNETCORE_URLS"] ?? "http://127.0.0.1:5200");
builder.Services.AddProblemDetails();
builder.Services.AddSingleton<MySqlStore>();
builder.Services.AddSingleton<IMailTransport, MailKitTransport>();
builder.Services.AddSingleton<IMailInbox, GraphMailInbox>();
builder.Services.AddSingleton<IIdentityDirectory, GraphIdentityDirectory>();
builder.Services.AddHostedService<FeatureSchemaStartup>();
builder.Services.AddHostedService<FeatureMailWorker>();
builder.Services.AddHostedService<FeatureInboxWorker>();
var authSettings = AuthSettings.Load(builder.Configuration);
var localPreviewEnabled = builder.Environment.IsDevelopment() && builder.Configuration.GetValue<bool>("ASTRA_LOCAL_TEST_LOGIN");
builder.Services.AddSingleton(authSettings);
builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    // Keep ordinary unauthenticated API calls as 401s when Microsoft sign-in
    // has not been configured. The login endpoint explicitly challenges OIDC.
    options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
})
.AddCookie(options =>
{
    // Keep the parallel migration preview isolated from the original app's cookie on localhost.
    options.Cookie.Name = builder.Configuration["ASTRA_SESSION_COOKIE_NAME"] ?? "astra_migration_session";
    options.Cookie.Path = "/api";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = Uri.TryCreate(authSettings.ApplicationBaseUrl, UriKind.Absolute, out var origin) && origin.Scheme == Uri.UriSchemeHttps
        ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
    options.ExpireTimeSpan = TimeSpan.FromSeconds(builder.Configuration.GetValue("SESSION_TTL_SECONDS", 28800));
    options.SlidingExpiration = false;
    options.Events.OnRedirectToLogin = context =>
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = context =>
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    };
    options.Events.OnValidatePrincipal = async context =>
    {
        if (context.Principal?.HasClaim("astra-local-preview", "true") == true &&
            (!localPreviewEnabled || !IsLocalPreviewRequest(context.HttpContext, authSettings)))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }
        var userId = context.Principal?.FindFirstValue("astra-user-id");
        var version = context.Principal?.FindFirstValue("astra-session-version");
        if (userId is null || version is null || !int.TryParse(version, out var parsed) ||
            !await context.HttpContext.RequestServices.GetRequiredService<MySqlStore>().SessionIsCurrent(userId, parsed, context.HttpContext.RequestAborted))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    };
})
.AddOpenIdConnect(options =>
{
    var settings = authSettings;
    options.Authority = settings.Authority;
    // ASP.NET validates every registered handler even before a request uses
    // it. Keep the dormant handler valid without inventing usable credentials.
    options.ClientId = string.IsNullOrEmpty(settings.ClientId) ? "00000000-0000-0000-0000-000000000000" : settings.ClientId;
    options.ClientSecret = string.IsNullOrEmpty(settings.ClientSecret) ? "migration-not-configured" : settings.ClientSecret;
    options.CallbackPath = "/api/auth/callback";
    options.ResponseType = OpenIdConnectResponseType.Code;
    options.UsePkce = true;
    options.SaveTokens = false;
    options.GetClaimsFromUserInfoEndpoint = false;
    options.MapInboundClaims = false;
    options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
    options.Scope.Clear();
    options.Scope.Add("openid");
    options.Scope.Add("profile");
    options.Scope.Add("email");
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidIssuer = settings.Issuer,
        ValidateAudience = true,
        ValidAudience = settings.ClientId,
        NameClaimType = "name"
    };
    options.Events.OnTokenValidated = async context =>
    {
        var principal = context.Principal;
        var tenant = principal?.FindFirstValue("tid");
        var objectId = principal?.FindFirstValue("oid");
        var email = principal?.FindFirstValue("preferred_username") ?? principal?.FindFirstValue("email");
        var displayName = principal?.FindFirstValue("name");
        if (tenant is null || objectId is null || email is null || displayName is null)
        {
            context.Fail("Required identity claims are missing.");
            return;
        }
        var account = await context.HttpContext.RequestServices.GetRequiredService<MySqlStore>()
            .UpdateKnownAccount(tenant, objectId, email, displayName, context.HttpContext.RequestAborted);
        if (account is null)
        {
            context.Fail("This account is not enabled for the application.");
            return;
        }
        if (principal?.Identity is not ClaimsIdentity identity)
        {
            context.Fail("The identity could not be established.");
            return;
        }
        identity.AddClaim(new Claim("astra-user-id", account.Id));
        identity.AddClaim(new Claim("astra-session-version", account.SessionVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        identity.AddClaim(new Claim("astra-csrf", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_')));
    };
    options.Events.OnRemoteFailure = context =>
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.HandleResponse();
        return Task.CompletedTask;
    };
});
builder.Services.AddAuthorization();

var app = builder.Build();
app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.XFrameOptions = "DENY";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    context.Response.Headers.CacheControl = "no-store";
    await next(context);
});
app.UseAuthentication();
app.UseAuthorization();

static IResult MigrationPending() => Results.Json(new
{
    error = new { code = "MIGRATION_INCOMPLETE", message = "The replacement application is not ready for use." }
}, statusCode: 503);

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/ready", MigrationPending);
app.MapGet("/api/ready", MigrationPending);

app.MapGet("/api/migration/database-health", async (CancellationToken cancellationToken) =>
{
    if (!app.Environment.IsDevelopment()) return Results.NotFound();
    var connectionString = builder.Configuration.GetConnectionString("MySql");
    if (string.IsNullOrWhiteSpace(connectionString))
        return Results.Json(new { status = "unconfigured" }, statusCode: 503);
    try
    {
        await using var connection = new MySqlConnection(connectionString);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await connection.OpenAsync(timeout.Token);
        await using var command = new MySqlCommand("SELECT 1", connection);
        await command.ExecuteScalarAsync(timeout.Token);
        return Results.Ok(new { status = "reachable" });
    }
    catch (Exception exception) when (exception is MySqlException or OperationCanceledException)
    {
        return Results.Json(new { status = "unavailable" }, statusCode: 503);
    }
});

app.MapGet("/api/auth/status", (HttpContext context, AuthSettings settings) =>
{
    var localTestLogin = localPreviewEnabled && IsLocalPreviewRequest(context, settings);
    return Results.Ok(new { configured = settings.Configured || localTestLogin, microsoftConfigured = settings.Configured, localTestLogin });
});
app.MapPost("/api/auth/local-test", async (HttpContext context, MySqlStore store, CancellationToken ct) =>
{
    if (!localPreviewEnabled || !IsLocalPreviewRequest(context, authSettings)) return Results.NotFound();
    if (!IsAllowedLocalPreviewOrigin(context, authSettings, context.Request.Headers.Origin.ToString()))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var account = await store.EnsureLocalPreviewAccount(ct);
    var identity = new ClaimsIdentity(new[]
    {
        new Claim("astra-user-id", account.Id),
        new Claim("astra-session-version", account.SessionVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        new Claim("astra-local-preview", "true"),
        new Claim("astra-csrf", Convert.ToHexString(RandomNumberGenerator.GetBytes(32)))
    }, CookieAuthenticationDefaults.AuthenticationScheme);
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
        new AuthenticationProperties { IsPersistent = false, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1) });
    return Results.Ok(new { authenticated = true });
}).AllowAnonymous();
app.MapGet("/api/auth/login", (string? returnTo, AuthSettings settings) =>
{
    if (!settings.Configured) return Results.Json(new { error = new { code = "AUTH_NOT_CONFIGURED", message = "Sign-in is not configured." } }, statusCode: 503);
    var destination = SafeReturnTo(returnTo);
    return Results.Challenge(new AuthenticationProperties { RedirectUri = destination }, [OpenIdConnectDefaults.AuthenticationScheme]);
}).AllowAnonymous();

app.MapGet("/api/auth/me", async (HttpContext context, MySqlStore store, CancellationToken cancellationToken) =>
{
    var id = context.User.FindFirstValue("astra-user-id");
    if (id is null) return Results.Unauthorized();
    var referer = context.Request.Headers.Referer.ToString();
    var sessionBaseUrl = SessionApplicationBaseUrl(context, authSettings);
    var workspaceOrigin = context.User.HasClaim("astra-local-preview", "true")
        ? new Uri(sessionBaseUrl) : new Uri($"{context.Request.Scheme}://{context.Request.Host}");
    var workspace = ResolveWorkspace(referer, workspaceOrigin.Scheme, workspaceOrigin.Authority, sessionBaseUrl);
    var user = await store.GetUserView(id, workspace, cancellationToken);
    if (user is null) return Results.Unauthorized();
    return Results.Ok(new { configured = true, authenticated = true, user, csrfToken = context.User.FindFirstValue("astra-csrf") });
}).RequireAuthorization();

app.MapPost("/api/auth/logout", async (HttpContext context, AuthSettings settings) =>
{
    var origin = context.Request.Headers.Origin.ToString();
    if (!Uri.TryCreate(SessionApplicationBaseUrl(context, settings), UriKind.Absolute, out var appUri) ||
        !string.Equals(origin, appUri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
        return Results.Forbid();
    var supplied = context.Request.Headers["x-csrf-token"].ToString();
    var expected = context.User.FindFirstValue("astra-csrf");
    if (expected is null || supplied.Length != expected.Length ||
        !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(expected)))
        return Results.Forbid();
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok(new { loggedOut = true });
}).RequireAuthorization();

app.MapGet("/api/tickets", async (HttpContext context, MySqlStore store, CancellationToken cancellationToken) =>
{
    var userId = context.User.FindFirstValue("astra-user-id");
    if (userId is null) return Results.Unauthorized();
    var query = context.Request.Query;
    var scope = query["scope"].FirstOrDefault() ?? "my";
    var status = query["status"].FirstOrDefault();
    var search = query["q"].FirstOrDefault()?.Trim() ?? "";
    if (scope is not ("my" or "team" or "all" or "internal" or "unassigned") || search.Length > 200 ||
        (status is not null && status is not ("NEW" or "OPEN" or "IN_PROGRESS" or "WAITING_FOR_CLIENT" or "WAITING_FOR_VENDOR" or "ON_HOLD" or "RESOLVED" or "CLOSED" or "CANCELLED")))
        return Results.BadRequest(new { error = new { code = "INVALID_FILTER", message = "One or more ticket filters are invalid." } });
    var page = int.TryParse(query["page"], out var requestedPage) ? Math.Clamp(requestedPage, 1, 100000) : 1;
    var pageSize = int.TryParse(query["pageSize"], out var requestedSize) ? Math.Clamp(requestedSize, 1, 100) : 25;
    TicketPage result;
    try { result = await store.ListTickets(userId, scope, status, search, page, pageSize, cancellationToken, filters: query); }
    catch (ArgumentException e) { return Results.BadRequest(new { error = new { code = "INVALID_FILTER", message = e.Message } }); }
    return result.Forbidden
        ? (IResult)Results.Forbid()
        : Results.Ok(new { total = result.Total, items = result.Items, page, pageSize });
}).RequireAuthorization();

app.MapGet("/api/tickets/dashboard", async (HttpContext context, MySqlStore store, CancellationToken cancellationToken) =>
{
    var userId = context.User.FindFirstValue("astra-user-id");
    if (userId is null) return Results.Unauthorized();
    var scope = context.Request.Query["scope"].FirstOrDefault() ?? "my";
    if (scope is not ("my" or "all")) return Results.BadRequest(new { error = new { code = "INVALID_FILTER", message = "The dashboard scope is invalid." } });
    var dashboard = await store.GetTicketDashboard(userId, scope == "all", cancellationToken);
    return dashboard is null ? Results.Forbid() : Results.Ok(dashboard);
}).RequireAuthorization();

app.MapGet("/api/tickets/options", async (HttpContext context, MySqlStore store, CancellationToken cancellationToken) =>
{
    var userId = context.User.FindFirstValue("astra-user-id");
    if (userId is null) return Results.Unauthorized();
    var result = await store.GetTicketOptions(userId, cancellationToken);
    return result is null ? Results.Forbid() : Results.Ok(result);
}).RequireAuthorization();

app.MapGet("/api/tickets/views", async (HttpContext context, MySqlStore store, CancellationToken cancellationToken) =>
{
    var userId = context.User.FindFirstValue("astra-user-id");
    return userId is null ? Results.Unauthorized() : Results.Ok(await store.GetSavedTicketViews(userId, cancellationToken));
}).RequireAuthorization();

app.MapGet("/api/roles", async (HttpContext context, MySqlStore store, CancellationToken cancellationToken) =>
{
    var userId = context.User.FindFirstValue("astra-user-id");
    if (userId is null) return Results.Unauthorized();
    if (!await store.IsAdministrator(userId, cancellationToken)) return Results.Forbid();
    return Results.Ok(await store.GetRoles(cancellationToken));
}).RequireAuthorization();

app.MapGet("/api/permissions", async (HttpContext context, MySqlStore store, CancellationToken cancellationToken) =>
{
    var userId = context.User.FindFirstValue("astra-user-id");
    if (userId is null) return Results.Unauthorized();
    if (!await store.IsAdministrator(userId, cancellationToken)) return Results.Forbid();
    return Results.Ok(await store.GetPermissions(cancellationToken));
}).RequireAuthorization();

app.MapGet("/api/sla/policies", async (HttpContext context, MySqlStore store, CancellationToken cancellationToken) =>
{
    var userId = context.User.FindFirstValue("astra-user-id");
    if (userId is null) return Results.Unauthorized();
    var policies = await store.ListSlaSettings(userId, "policies", cancellationToken);
    return policies is null ? Results.Forbid() : Results.Ok(policies);
}).RequireAuthorization();

app.MapGet("/api/sla/calendars", async (HttpContext context, MySqlStore store, CancellationToken cancellationToken) =>
{
    var userId = context.User.FindFirstValue("astra-user-id");
    if (userId is null) return Results.Unauthorized();
    var calendars = await store.ListSlaSettings(userId, "calendars", cancellationToken);
    return calendars is null ? Results.Forbid() : Results.Ok(calendars);
}).RequireAuthorization();

app.MapPost("/api/sla/calendars", async (SlaCalendarRequest body, HttpContext context, MySqlStore store, AuthSettings settings, CancellationToken ct) =>
{
    if (!HasValidCsrf(context, settings)) return Results.Forbid();
    var userId = context.User.FindFirstValue("astra-user-id");
    if (userId is null) return Results.Unauthorized();
    try { var created = await store.SaveSlaCalendar(userId, null, body, ct); return created is null ? Results.Forbid() : Results.Created($"/api/sla/calendars/{created.Id}", created); }
    catch (ArgumentException exception) { return Results.BadRequest(new { error = new { code = "INVALID_SLA_CALENDAR", message = exception.Message } }); }
}).RequireAuthorization();

app.MapPatch("/api/sla/calendars/{id}", async (string id, SlaCalendarRequest body, HttpContext context, MySqlStore store, AuthSettings settings, CancellationToken ct) =>
{
    if (!HasValidCsrf(context, settings)) return Results.Forbid();
    var userId = context.User.FindFirstValue("astra-user-id");
    if (userId is null) return Results.Unauthorized();
    if (!Guid.TryParse(id, out _)) return Results.BadRequest(new { error = new { code = "INVALID_SLA_CALENDAR", message = "The calendar ID is invalid." } });
    try { var saved = await store.SaveSlaCalendar(userId, id, body, ct); return saved is null ? Results.Forbid() : Results.Ok(saved); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (ArgumentException exception) { return Results.BadRequest(new { error = new { code = "INVALID_SLA_CALENDAR", message = exception.Message } }); }
}).RequireAuthorization();
app.MapPatch("/api/sla/policies/{priority}", async (string priority, SlaPolicyRequest body, HttpContext context, MySqlStore store, AuthSettings settings, CancellationToken ct) =>
{
    if (!HasValidCsrf(context, settings)) return Results.Forbid();
    var userId = context.User.FindFirstValue("astra-user-id");
    if (userId is null) return Results.Unauthorized();
    try { var saved = await store.SaveSlaPolicy(userId, priority, body, ct); return saved is null ? Results.Forbid() : Results.Ok(saved); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (ArgumentException exception) { return Results.BadRequest(new { error = new { code = "INVALID_SLA_POLICY", message = exception.Message } }); }
}).RequireAuthorization();

app.MapGet("/api/ticket-sources", async (HttpContext context, MySqlStore store, CancellationToken cancellationToken) =>
{
    var userId = context.User.FindFirstValue("astra-user-id");
    return userId is null ? Results.Unauthorized() : Results.Ok(await store.GetTicketSources(cancellationToken));
}).RequireAuthorization();

app.MapGet("/api/clients", async (HttpContext context, MySqlStore store, CancellationToken cancellationToken) =>
{
    var userId = context.User.FindFirstValue("astra-user-id");
    return userId is null ? Results.Unauthorized() : Results.Ok(await store.ListClients(userId, cancellationToken));
}).RequireAuthorization();
app.MapGet("/api/contacts", async (string? clientId, HttpContext context, MySqlStore store, CancellationToken cancellationToken) =>
{
    var userId = context.User.FindFirstValue("astra-user-id");
    if (userId is null) return Results.Unauthorized();
    if (clientId is not null && !Guid.TryParse(clientId, out _)) return Results.BadRequest(new { error = new { code = "INVALID_CLIENT", message = "The client filter is invalid." } });
    return Results.Ok(await store.ListClientDirectory(userId, clientId, "contacts", cancellationToken));
}).RequireAuthorization();
app.MapGet("/api/sites", async (string? clientId, HttpContext context, MySqlStore store, CancellationToken cancellationToken) =>
{
    var userId = context.User.FindFirstValue("astra-user-id");
    if (userId is null) return Results.Unauthorized();
    if (clientId is not null && !Guid.TryParse(clientId, out _)) return Results.BadRequest(new { error = new { code = "INVALID_CLIENT", message = "The client filter is invalid." } });
    return Results.Ok(await store.ListClientDirectory(userId, clientId, "sites", cancellationToken));
}).RequireAuthorization();
app.MapGet("/api/assets", async (string? clientId,HttpContext context,MySqlStore store,CancellationToken ct)=>
{
    var userId=context.User.FindFirstValue("astra-user-id");if(userId is null)return Results.Unauthorized();if(clientId is not null&&!Guid.TryParse(clientId,out _))return Results.BadRequest(new{error=new{code="INVALID_CLIENT",message="The client filter is invalid."}});return Results.Ok(await store.ListClientDirectory(userId,clientId,"assets",ct));
}).RequireAuthorization();
app.MapGet("/api/categories", async (HttpContext context, MySqlStore store, CancellationToken cancellationToken) =>
{
    var userId = context.User.FindFirstValue("astra-user-id");
    return userId is null ? Results.Unauthorized() : Results.Ok(await store.ListCategories(cancellationToken));
}).RequireAuthorization();
app.MapGet("/api/tags", async (HttpContext context, MySqlStore store, CancellationToken cancellationToken) =>
{
    var userId = context.User.FindFirstValue("astra-user-id");
    return userId is null ? Results.Unauthorized() : Results.Ok(await store.ListTags(cancellationToken));
}).RequireAuthorization();
app.MapGet("/api/users", async (HttpContext context, MySqlStore store, CancellationToken cancellationToken) =>
{
    var userId = context.User.FindFirstValue("astra-user-id");
    if (userId is null) return Results.Unauthorized();
    if (!await store.IsAdministrator(userId, cancellationToken)) return Results.Forbid();
    return Results.Ok(await store.ListUsers(cancellationToken));
}).RequireAuthorization();
app.MapGet("/api/audit-logs", async (HttpContext context, MySqlStore store, CancellationToken cancellationToken) =>
{
    var userId = context.User.FindFirstValue("astra-user-id");
    if (userId is null) return Results.Unauthorized();
    if (!await store.IsAdministrator(userId, cancellationToken)) return Results.Forbid();
    return Results.Ok(await store.ListAuditLogs(cancellationToken));
}).RequireAuthorization();
app.MapGet("/api/admin/{resource}", async (string resource,string? q,string? status,string? clientId,int? page,int? pageSize,HttpContext context,MySqlStore store,CancellationToken ct)=>
{
    var userId=context.User.FindFirstValue("astra-user-id");if(userId is null)return Results.Unauthorized();if(status is not(null or "all" or "active" or "inactive")||(clientId is not null&&!Guid.TryParse(clientId,out _)))return Results.BadRequest(new{error=new{code="INVALID_FILTER",message="The administration filter is invalid."}});
    var result=await store.ListAdminResource(userId,resource,q??"",status??"all",clientId,Math.Clamp(page??1,1,100000),Math.Clamp(pageSize??20,1,100),ct);return result is null?Results.NotFound():Results.Ok(result);
}).RequireAuthorization();
app.MapGet("/api/admin/overview", async (HttpContext context, MySqlStore store, CancellationToken ct) =>
{
    var userId = context.User.FindFirstValue("astra-user-id");
    if (userId is null) return Results.Unauthorized();
    var overview = await store.GetAdminOverview(userId, ct);
    return overview is null ? Results.Forbid() : Results.Ok(overview);
}).RequireAuthorization();
app.MapGet("/api/admin/access-policies", async (HttpContext context, MySqlStore store, CancellationToken ct) =>
{
    var userId = context.User.FindFirstValue("astra-user-id");
    if (userId is null) return Results.Unauthorized();
    var policies = await store.GetAccessPolicies(userId, ct);
    return policies is null ? Results.Forbid() : Results.Ok(policies);
}).RequireAuthorization();
app.MapGet("/api/admin/access-policies/users/{id}", async (string id, HttpContext context, MySqlStore store, CancellationToken ct) =>
{
    var userId = context.User.FindFirstValue("astra-user-id");
    if (userId is null) return Results.Unauthorized();
    if (!Guid.TryParse(id, out _)) return Results.BadRequest(new { error = new { code = "INVALID_USER", message = "The user ID is invalid." } });
    if (!await store.IsAdministrator(userId, ct)) return Results.Forbid();
    var policy = await store.GetUserAccessPolicy(userId, id, ct);
    return policy is null ? Results.NotFound() : Results.Ok(policy);
}).RequireAuthorization();
app.MapPatch("/api/admin/access-policies/roles/{role}", async (string role, RolePolicyRequest body, HttpContext context, MySqlStore store, AuthSettings settings, CancellationToken ct) =>
{
    if (!HasValidCsrf(context, settings)) return Results.Forbid();
    var actorId = context.User.FindFirstValue("astra-user-id");
    if (actorId is null) return Results.Unauthorized();
    try { var saved = await store.SaveAccessPolicy(actorId, "role", role, body.Revision, body.Permissions, [], ct); return saved is null ? Results.Forbid() : Results.Ok(saved); }
    catch (ArgumentException exception) { return Results.BadRequest(new { error = new { code = "INVALID_POLICY", message = exception.Message } }); }
    catch (InvalidOperationException exception) { return Results.Conflict(new { error = new { code = "POLICY_CONFLICT", message = exception.Message } }); }
}).RequireAuthorization();
app.MapPatch("/api/admin/access-policies/users/{id}", async (string id, UserPolicyRequest body, HttpContext context, MySqlStore store, AuthSettings settings, CancellationToken ct) =>
{
    if (!HasValidCsrf(context, settings)) return Results.Forbid();
    var actorId = context.User.FindFirstValue("astra-user-id");
    if (actorId is null) return Results.Unauthorized();
    if (!Guid.TryParse(id, out _)) return Results.BadRequest(new { error = new { code = "INVALID_USER", message = "The user ID is invalid." } });
    try { var saved = await store.SaveAccessPolicy(actorId, "user", id, body.Revision, body.Allow, body.Deny, ct); return saved is null ? Results.Forbid() : Results.Ok(saved); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (ArgumentException exception) { return Results.BadRequest(new { error = new { code = "INVALID_POLICY", message = exception.Message } }); }
    catch (InvalidOperationException exception) { return Results.Conflict(new { error = new { code = "POLICY_CONFLICT", message = exception.Message } }); }
}).RequireAuthorization();
app.MapGet("/api/notifications", async (int? limit, HttpContext context, MySqlStore store, CancellationToken ct) =>
{
    var userId = context.User.FindFirstValue("astra-user-id");
    return userId is null ? Results.Unauthorized() : Results.Ok(await store.ListNotifications(userId, Math.Clamp(limit ?? 20, 1, 50), ct));
}).RequireAuthorization();
app.MapGet("/api/notifications/preferences", async (HttpContext context, MySqlStore store, CancellationToken ct) =>
{
    var userId = context.User.FindFirstValue("astra-user-id");
    return userId is null ? Results.Unauthorized() : Results.Ok(await store.GetNotificationPreferences(userId, ct));
}).RequireAuthorization();
app.MapPatch("/api/notifications/preferences", async (NotificationPreferencesRequest body, HttpContext context, MySqlStore store, AuthSettings settings, CancellationToken ct) =>
{
    if (!HasValidCsrf(context, settings)) return Results.Forbid();
    var userId = context.User.FindFirstValue("astra-user-id");
    if (userId is null) return Results.Unauthorized();
    await store.SaveNotificationPreferences(userId, body, ct);
    return Results.Ok(body);
}).RequireAuthorization();
app.MapPatch("/api/notifications/read-all", async (HttpContext context, MySqlStore store, AuthSettings settings, CancellationToken ct) =>
{
    if (!HasValidCsrf(context, settings)) return Results.Forbid();
    var userId = context.User.FindFirstValue("astra-user-id");
    if (userId is null) return Results.Unauthorized();
    await store.MarkNotificationsRead(userId, null, ct);
    return Results.Ok(new { read = true });
}).RequireAuthorization();
app.MapPatch("/api/notifications/{id}/read", async (string id, HttpContext context, MySqlStore store, AuthSettings settings, CancellationToken ct) =>
{
    if (!HasValidCsrf(context, settings)) return Results.Forbid();
    var userId = context.User.FindFirstValue("astra-user-id");
    if (userId is null) return Results.Unauthorized();
    if (!Guid.TryParse(id, out _)) return Results.BadRequest(new { error = new { code = "INVALID_NOTIFICATION", message = "The notification ID is invalid." } });
    await store.MarkNotificationsRead(userId, id, ct);
    return Results.Ok(new { id });
}).RequireAuthorization();
app.MapGet("/api/internal/departments", async (HttpContext context, MySqlStore store, CancellationToken ct) =>
{
    var userId = context.User.FindFirstValue("astra-user-id");
    return userId is null ? Results.Unauthorized() : Results.Ok(await store.ListDepartments(ct));
}).RequireAuthorization();
app.MapGet("/api/internal/directory", async (HttpContext context, MySqlStore store, CancellationToken ct) =>
{
    var userId = context.User.FindFirstValue("astra-user-id");
    if (userId is null) return Results.Unauthorized();
    var people = await store.ListStaffDirectory(userId, ct);
    return people is null ? Results.Forbid() : Results.Ok(people);
}).RequireAuthorization();
app.MapPost("/api/internal/departments", async (DepartmentRequest body, HttpContext context, MySqlStore store, AuthSettings settings, CancellationToken ct) =>
{
    if (!HasValidCsrf(context, settings)) return Results.Forbid();
    var userId = context.User.FindFirstValue("astra-user-id");
    if (userId is null) return Results.Unauthorized();
    try { var created = await store.CreateDepartment(userId, body, ct); return created is null ? Results.Forbid() : Results.Created($"/api/internal/departments/{created.Id}", created); }
    catch (ArgumentException exception) { return Results.BadRequest(new { error = new { code = "INVALID_DEPARTMENT", message = exception.Message } }); }
}).RequireAuthorization();
app.MapPost("/api/internal/departments/{id}/members", async (string id, DepartmentMemberRequest body, HttpContext context, MySqlStore store, AuthSettings settings, CancellationToken ct) =>
{
    if (!HasValidCsrf(context, settings)) return Results.Forbid();
    var actorId = context.User.FindFirstValue("astra-user-id");
    if (actorId is null) return Results.Unauthorized();
    if (!Guid.TryParse(id, out _)) return Results.BadRequest(new { error = new { code = "INVALID_DEPARTMENT", message = "The department ID is invalid." } });
    try { var saved = await store.SaveDepartmentMember(actorId, id, body, ct); return saved is null ? Results.Forbid() : Results.Ok(saved); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (ArgumentException exception) { return Results.BadRequest(new { error = new { code = "INVALID_DEPARTMENT_MEMBER", message = exception.Message } }); }
}).RequireAuthorization();
app.MapGet("/api/knowledge/options", async (HttpContext context, MySqlStore store, CancellationToken ct) =>
{
    var userId = context.User.FindFirstValue("astra-user-id");
    return userId is null ? Results.Unauthorized() : Results.Ok(await store.GetKnowledgeOptions(userId, ct));
}).RequireAuthorization();
app.MapGet("/api/knowledge/catalogue", async (HttpContext context, MySqlStore store, CancellationToken ct) =>
{
    var userId = context.User.FindFirstValue("astra-user-id");
    return userId is null ? Results.Unauthorized() : Results.Ok(await store.GetServiceCatalogue(userId, ct));
}).RequireAuthorization();
app.MapGet("/api/knowledge", async (string? q, int? page, HttpContext context, MySqlStore store, CancellationToken ct) =>
{
    var userId = context.User.FindFirstValue("astra-user-id");
    if (userId is null) return Results.Unauthorized();
    var result = await store.ListKnowledge(userId, q ?? "", Math.Clamp(page ?? 1, 1, 100000), null, ct);
    return result is null ? Results.Forbid() : Results.Ok(result);
}).RequireAuthorization();
app.MapGet("/api/knowledge/{id}", async (string id, HttpContext context, MySqlStore store, CancellationToken ct) =>
{
    var userId = context.User.FindFirstValue("astra-user-id");
    if (userId is null) return Results.Unauthorized();
    if (!Guid.TryParse(id, out _)) return Results.BadRequest(new { error = new { code = "INVALID_ARTICLE", message = "The article ID is invalid." } });
    var result = await store.ListKnowledge(userId, "", 1, id, ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
}).RequireAuthorization();
app.MapPost("/api/knowledge/catalogue", async (ServiceTemplateRequest body, HttpContext context, MySqlStore store, AuthSettings settings, CancellationToken ct) =>
{
    if (!HasValidCsrf(context, settings)) return Results.Forbid();
    var userId = context.User.FindFirstValue("astra-user-id");
    if (userId is null) return Results.Unauthorized();
    try { var saved = await store.SaveServiceTemplate(userId, null, body, ct); return saved is null ? Results.Forbid() : Results.Created($"/api/knowledge/catalogue/{saved.Id}", saved); }
    catch (ArgumentException exception) { return Results.BadRequest(new { error = new { code = "INVALID_TEMPLATE", message = exception.Message } }); }
}).RequireAuthorization();
app.MapPatch("/api/knowledge/catalogue/{id}", async (string id, ServiceTemplateRequest body, HttpContext context, MySqlStore store, AuthSettings settings, CancellationToken ct) =>
{
    if (!HasValidCsrf(context, settings)) return Results.Forbid();
    var userId = context.User.FindFirstValue("astra-user-id");
    if (userId is null) return Results.Unauthorized();
    if (!Guid.TryParse(id, out _)) return Results.BadRequest(new { error = new { code = "INVALID_TEMPLATE", message = "The template ID is invalid." } });
    try { var saved = await store.SaveServiceTemplate(userId, id, body, ct); return saved is null ? Results.Forbid() : Results.Ok(saved); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (ArgumentException exception) { return Results.BadRequest(new { error = new { code = "INVALID_TEMPLATE", message = exception.Message } }); }
}).RequireAuthorization();
app.MapPost("/api/knowledge", async (KnowledgeArticleRequest body, HttpContext context, MySqlStore store, AuthSettings settings, CancellationToken ct) =>
{
    if (!HasValidCsrf(context, settings)) return Results.Forbid();
    var userId = context.User.FindFirstValue("astra-user-id");
    if (userId is null) return Results.Unauthorized();
    try { var saved = await store.SaveKnowledgeArticle(userId, null, body, ct); return saved is null ? Results.Forbid() : Results.Created($"/api/knowledge/{saved.Id}", saved); }
    catch (ArgumentException exception) { return Results.BadRequest(new { error = new { code = "INVALID_ARTICLE", message = exception.Message } }); }
}).RequireAuthorization();
app.MapPatch("/api/knowledge/{id}", async (string id, KnowledgeArticleRequest body, HttpContext context, MySqlStore store, AuthSettings settings, CancellationToken ct) =>
{
    if (!HasValidCsrf(context, settings)) return Results.Forbid();
    var userId = context.User.FindFirstValue("astra-user-id");
    if (userId is null) return Results.Unauthorized();
    if (!Guid.TryParse(id, out _)) return Results.BadRequest(new { error = new { code = "INVALID_ARTICLE", message = "The article ID is invalid." } });
    try { var saved = await store.SaveKnowledgeArticle(userId, id, body, ct); return saved is null ? Results.Forbid() : Results.Ok(saved); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (InvalidOperationException exception) { return Results.Conflict(new { error = new { code = "ARTICLE_CONFLICT", message = exception.Message } }); }
    catch (ArgumentException exception) { return Results.BadRequest(new { error = new { code = "INVALID_ARTICLE", message = exception.Message } }); }
}).RequireAuthorization();
app.MapPost("/api/knowledge/{id}/tickets", async (string id, KnowledgeTicketLinkRequest body, HttpContext context, MySqlStore store, AuthSettings settings, CancellationToken ct) =>
{
    if (!HasValidCsrf(context, settings)) return Results.Forbid();
    var userId = context.User.FindFirstValue("astra-user-id");
    if (userId is null) return Results.Unauthorized();
    if (!Guid.TryParse(id, out _) || !Guid.TryParse(body.TicketId, out _)) return Results.BadRequest(new { error = new { code = "INVALID_LINK", message = "Select a valid article and ticket." } });
    try { var linked = await store.LinkKnowledgeTicket(userId, id, body.TicketId, ct); return linked is null ? Results.NotFound() : Results.Ok(linked); }
    catch (ArgumentException exception) { return Results.BadRequest(new { error = new { code = "INVALID_LINK", message = exception.Message } }); }
}).RequireAuthorization();
app.MapGet("/api/problems/options", async (HttpContext context, MySqlStore store, CancellationToken ct) =>
{
    var userId = context.User.FindFirstValue("astra-user-id");
    if (userId is null) return Results.Unauthorized();
    var options = await store.GetProblemOptions(userId, ct);
    return options is null ? Results.Forbid() : Results.Ok(options);
}).RequireAuthorization();
app.MapGet("/api/problems", async (string? q, string? status, string? clientId, int? page, int? pageSize, HttpContext context, MySqlStore store, CancellationToken ct) =>
{
    var userId = context.User.FindFirstValue("astra-user-id");
    if (userId is null) return Results.Unauthorized();
    if (status is not (null or "OPEN" or "INVESTIGATING" or "KNOWN_ERROR" or "RESOLVED" or "CLOSED") || (clientId is not null && !Guid.TryParse(clientId, out _))) return Results.BadRequest(new { error = new { code = "INVALID_FILTER", message = "The problem filter is invalid." } });
    var problems = await store.ListProblems(userId, q ?? "", status, clientId, Math.Clamp(page ?? 1, 1, 100000), Math.Clamp(pageSize ?? 20, 1, 100), ct);
    return problems is null ? Results.Forbid() : Results.Ok(problems);
}).RequireAuthorization();
app.MapPost("/api/problems", async (CreateProblemRequest body, HttpContext context, MySqlStore store, AuthSettings settings, CancellationToken ct) =>
{
    if (!HasValidCsrf(context, settings)) return Results.Forbid();
    var userId = context.User.FindFirstValue("astra-user-id");
    if (userId is null) return Results.Unauthorized();
    try { var created = await store.CreateProblem(userId, body, ct); return created is null ? Results.Forbid() : Results.Created($"/api/problems/{created.Id}", created); }
    catch (ArgumentException exception) { return Results.BadRequest(new { error = new { code = "INVALID_PROBLEM", message = exception.Message } }); }
}).RequireAuthorization();
app.MapGet("/api/problems/{id}", async (string id, HttpContext context, MySqlStore store, CancellationToken ct) =>
{
    var userId = context.User.FindFirstValue("astra-user-id");
    if (userId is null) return Results.Unauthorized();
    if (!Guid.TryParse(id, out _)) return Results.BadRequest(new { error = new { code = "INVALID_PROBLEM", message = "The problem ID is invalid." } });
    var detail = await store.GetProblemDetail(userId, id, ct);
    return detail is null ? Results.NotFound() : Results.Ok(detail);
}).RequireAuthorization();
app.MapPost("/api/problems/{id}/tickets", async (string id, ProblemTicketRequest body, HttpContext context, MySqlStore store, AuthSettings settings, CancellationToken ct) =>
{
    if (!HasValidCsrf(context, settings)) return Results.Forbid();
    var userId = context.User.FindFirstValue("astra-user-id");
    if (userId is null) return Results.Unauthorized();
    if (!Guid.TryParse(id, out _) || !Guid.TryParse(body.TicketId, out _)) return Results.BadRequest(new { error = new { code = "INVALID_LINK", message = "Select a valid problem and ticket." } });
    try { var result = await store.ChangeProblemTicketLink(userId, id, body.TicketId, false, ct); return result is null ? Results.NotFound() : Results.Ok(result); }
    catch (ArgumentException exception) { return Results.BadRequest(new { error = new { code = "INVALID_LINK", message = exception.Message } }); }
}).RequireAuthorization();
app.MapDelete("/api/problems/{id}/tickets/{ticketId}", async (string id, string ticketId, HttpContext context, MySqlStore store, AuthSettings settings, CancellationToken ct) =>
{
    if (!HasValidCsrf(context, settings)) return Results.Forbid();
    var userId = context.User.FindFirstValue("astra-user-id");
    if (userId is null) return Results.Unauthorized();
    if (!Guid.TryParse(id, out _) || !Guid.TryParse(ticketId, out _)) return Results.BadRequest(new { error = new { code = "INVALID_LINK", message = "Select a valid problem and ticket." } });
    try { var result = await store.ChangeProblemTicketLink(userId, id, ticketId, true, ct); return result is null ? Results.NotFound() : Results.Ok(result); }
    catch (ArgumentException exception) { return Results.BadRequest(new { error = new { code = "INVALID_LINK", message = exception.Message } }); }
}).RequireAuthorization();

app.MapGet("/api/help/topics", async (HttpContext context, MySqlStore store, CancellationToken ct) =>
{
    var userId = context.User.FindFirstValue("astra-user-id");
    if (userId is null) return Results.Unauthorized();
    var topics = await store.GetHelpTopics(userId, ct);
    return topics is null ? Results.Forbid() : Results.Ok(topics);
}).RequireAuthorization();

app.MapPost("/api/contacts", async (CreateContactRequest body, HttpContext context, MySqlStore store, AuthSettings settings, CancellationToken ct) =>
{
    if (!HasValidCsrf(context, settings)) return Results.Forbid(); var userId=context.User.FindFirstValue("astra-user-id"); if(userId is null)return Results.Unauthorized();
    try { var created=await store.CreateContact(userId,body,ct); return created is null?Results.Forbid():Results.Created($"/api/contacts/{created.Id}",created); }
    catch(UnauthorizedAccessException){return Results.Forbid();} catch(ArgumentException ex){return Results.BadRequest(new{error=new{code="INVALID_CONTACT",message=ex.Message}});}
}).RequireAuthorization();
app.MapPost("/api/clients", async (CreateClientRequest body,HttpContext context,MySqlStore store,AuthSettings settings,CancellationToken ct)=>
{
    if(!HasValidCsrf(context,settings))return Results.Forbid();var userId=context.User.FindFirstValue("astra-user-id");if(userId is null)return Results.Unauthorized();
    try{var result=await store.CreateClient(userId,body,ct);return result is null?Results.Forbid():Results.Created($"/api/clients/{result.Id}",result);}catch(UnauthorizedAccessException){return Results.Forbid();}catch(ArgumentException ex){return Results.BadRequest(new{error=new{code="INVALID_CLIENT",message=ex.Message}});}
}).RequireAuthorization();
app.MapPost("/api/sites", async (CreateSiteRequest body,HttpContext context,MySqlStore store,AuthSettings settings,CancellationToken ct)=>
{
    if(!HasValidCsrf(context,settings))return Results.Forbid();var userId=context.User.FindFirstValue("astra-user-id");if(userId is null)return Results.Unauthorized();
    try{var result=await store.CreateSite(userId,body,ct);return result is null?Results.Forbid():Results.Created($"/api/sites/{result.Id}",result);}catch(UnauthorizedAccessException){return Results.Forbid();}catch(ArgumentException ex){return Results.BadRequest(new{error=new{code="INVALID_SITE",message=ex.Message}});}
}).RequireAuthorization();
app.MapPost("/api/assets",async(CreateAssetRequest body,HttpContext context,MySqlStore store,AuthSettings settings,CancellationToken ct)=>
{
    if(!HasValidCsrf(context,settings))return Results.Forbid();var userId=context.User.FindFirstValue("astra-user-id");if(userId is null)return Results.Unauthorized();try{var result=await store.CreateAsset(userId,body,ct);return result is null?Results.Forbid():Results.Created($"/api/assets/{result.Id}",result);}catch(UnauthorizedAccessException){return Results.Forbid();}catch(ArgumentException ex){return Results.BadRequest(new{error=new{code="INVALID_ASSET",message=ex.Message}});}
}).RequireAuthorization();
app.MapPost("/api/categories", async (CreateCategoryRequest body, HttpContext context, MySqlStore store, AuthSettings settings, CancellationToken ct) =>
{
    if(!HasValidCsrf(context,settings))return Results.Forbid();var userId=context.User.FindFirstValue("astra-user-id");if(userId is null)return Results.Unauthorized();
    try{var created=await store.CreateCategory(userId,body,ct);return created is null?Results.Forbid():Results.Created($"/api/categories/{created.Id}",created);}catch(UnauthorizedAccessException){return Results.Forbid();}catch(ArgumentException ex){return Results.BadRequest(new{error=new{code="INVALID_CATEGORY",message=ex.Message}});}
}).RequireAuthorization();
app.MapPost("/api/tags", async (CreateTagRequest body, HttpContext context, MySqlStore store, AuthSettings settings, CancellationToken ct) =>
{
    if(!HasValidCsrf(context,settings))return Results.Forbid();var userId=context.User.FindFirstValue("astra-user-id");if(userId is null)return Results.Unauthorized();
    try{var created=await store.CreateTag(userId,body,ct);return created is null?Results.Forbid():Results.Created($"/api/tags/{created.Id}",created);}catch(UnauthorizedAccessException){return Results.Forbid();}catch(ArgumentException ex){return Results.BadRequest(new{error=new{code="INVALID_TAG",message=ex.Message}});}
}).RequireAuthorization();
app.MapPost("/api/ticket-sources", async (TicketSourceRequest body, HttpContext context, MySqlStore store, AuthSettings settings, CancellationToken ct) =>
{
    if(!HasValidCsrf(context,settings))return Results.Forbid();var userId=context.User.FindFirstValue("astra-user-id");if(userId is null)return Results.Unauthorized();
    try{var created=await store.CreateTicketSource(userId,body,ct);return created is null?Results.Forbid():Results.Created($"/api/ticket-sources/{created.Code}",created);}catch(UnauthorizedAccessException){return Results.Forbid();}catch(ArgumentException ex){return Results.BadRequest(new{error=new{code="INVALID_TICKET_SOURCE",message=ex.Message}});}
}).RequireAuthorization();
app.MapPost("/api/sources", async (TicketSourceRequest body, HttpContext context, MySqlStore store, AuthSettings settings, CancellationToken ct) =>
{
    if(!HasValidCsrf(context,settings))return Results.Forbid();var userId=context.User.FindFirstValue("astra-user-id");if(userId is null)return Results.Unauthorized();
    try{var created=await store.CreateTicketSource(userId,body,ct);return created is null?Results.Forbid():Results.Created($"/api/ticket-sources/{created.Code}",created);}catch(UnauthorizedAccessException){return Results.Forbid();}catch(ArgumentException ex){return Results.BadRequest(new{error=new{code="INVALID_TICKET_SOURCE",message=ex.Message}});}
}).RequireAuthorization();
app.MapPatch("/api/ticket-sources/{code}", async (string code, UpdateTicketSourceRequest body, HttpContext context, MySqlStore store, AuthSettings settings, CancellationToken ct) =>
{
    if(!HasValidCsrf(context,settings))return Results.Forbid();var userId=context.User.FindFirstValue("astra-user-id");if(userId is null)return Results.Unauthorized();
    try{var updated=await store.UpdateTicketSource(userId,code,body,ct);return updated is null?Results.NotFound():Results.Ok(updated);}catch(UnauthorizedAccessException){return Results.Forbid();}catch(ArgumentException ex){return Results.BadRequest(new{error=new{code="INVALID_TICKET_SOURCE",message=ex.Message}});}
}).RequireAuthorization();

app.MapPost("/api/tickets/views", async (SaveTicketViewRequest body, HttpContext context, MySqlStore store, AuthSettings settings, CancellationToken cancellationToken) =>
{
    if (!HasValidCsrf(context, settings)) return Results.Forbid();
    var userId = context.User.FindFirstValue("astra-user-id");
    if (userId is null) return Results.Unauthorized();
    try { return Results.Ok(await store.SaveTicketView(userId, body, cancellationToken)); }
    catch (ArgumentException exception) { return Results.BadRequest(new { error = new { code = "INVALID_SAVED_VIEW", message = exception.Message } }); }
}).RequireAuthorization();

app.MapDelete("/api/tickets/views/{id}", async (string id, HttpContext context, MySqlStore store, AuthSettings settings, CancellationToken cancellationToken) =>
{
    if (!HasValidCsrf(context, settings)) return Results.Forbid();
    var userId = context.User.FindFirstValue("astra-user-id");
    if (userId is null) return Results.Unauthorized();
    if (!Guid.TryParse(id, out _)) return Results.NotFound();
    return await store.DeleteSavedTicketView(userId, id, cancellationToken) ? Results.Ok(new { id, deleted = true }) : Results.NotFound();
}).RequireAuthorization();

app.MapGet("/api/tickets/{id}", async (string id, HttpContext context, MySqlStore store, CancellationToken cancellationToken) =>
{
    var userId = context.User.FindFirstValue("astra-user-id");
    if (userId is null) return Results.Unauthorized();
    if (!Guid.TryParse(id, out _)) return Results.NotFound();
    var detail = await store.GetTicketDetail(userId, id, cancellationToken);
    return detail is null ? Results.NotFound() : Results.Ok(detail);
}).RequireAuthorization();

app.MapPost("/api/tickets/requesters", async (CreateRequesterRequest body, HttpContext context, MySqlStore store, AuthSettings settings, CancellationToken cancellationToken) =>
{
    if (!HasValidCsrf(context, settings)) return Results.Forbid();
    var userId = context.User.FindFirstValue("astra-user-id");
    if (userId is null) return Results.Unauthorized();
    try
    {
        var created = await store.CreateRequester(userId, body, cancellationToken);
        return created is null ? Results.Forbid() : Results.Created($"/api/tickets/requesters/{created.Id}", created);
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = new { code = "INVALID_REQUESTER", message = exception.Message } });
    }
}).RequireAuthorization();

app.MapPost("/api/tickets", async (CreateTicketRequest body, HttpContext context, MySqlStore store, AuthSettings settings, CancellationToken cancellationToken) =>
{
    if (!HasValidCsrf(context, settings)) return Results.Forbid();
    var userId = context.User.FindFirstValue("astra-user-id");
    if (userId is null) return Results.Unauthorized();
    try
    {
        var created = await store.CreateTicket(userId, body, cancellationToken);
        return Results.Created($"/api/tickets/{created.Id}", created);
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = new { code = "INVALID_TICKET", message = exception.Message } });
    }
    catch (UnauthorizedAccessException)
    {
        return Results.Forbid();
    }
}).RequireAuthorization();

app.MapPost("/api/tickets/{id}/comments", async (string id, CreateTicketCommentRequest body, HttpContext context, MySqlStore store, AuthSettings settings, CancellationToken cancellationToken) =>
{
    if (!HasValidCsrf(context, settings)) return Results.Forbid();
    var userId = context.User.FindFirstValue("astra-user-id");
    if (userId is null) return Results.Unauthorized();
    if (!Guid.TryParse(id, out _) || body.Type is not ("PUBLIC" or "INTERNAL") || string.IsNullOrWhiteSpace(body.Body) || body.Body.Trim().Length > 20000)
        return Results.BadRequest(new { error = new { code = "INVALID_COMMENT", message = "Enter a comment and select a valid comment type." } });
    try
    {
        var created = await store.CreateTicketComment(userId, id, body, cancellationToken);
        return created is null ? Results.NotFound() : Results.Created($"/api/tickets/{id}/comments/{created.Id}", created);
    }
    catch (UnauthorizedAccessException)
    {
        return Results.Forbid();
    }
}).RequireAuthorization();

app.MapPost("/api/tickets/{id}/assign", async (string id, AssignTicketRequest body, HttpContext context, MySqlStore store, AuthSettings settings, CancellationToken cancellationToken) =>
{
    if (!HasValidCsrf(context, settings)) return Results.Forbid();
    var userId = context.User.FindFirstValue("astra-user-id");
    if (userId is null) return Results.Unauthorized();
    if (!Guid.TryParse(id, out _) || !Guid.TryParse(body.AssignedUserId, out _) || (body.Reason?.Length ?? 0) > 1000)
        return Results.BadRequest(new { error = new { code = "INVALID_ASSIGNMENT", message = "Select an active team member." } });
    try
    {
        var result = await store.AssignTicket(userId, id, body, cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }
    catch (UnauthorizedAccessException)
    {
        return Results.Forbid();
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = new { code = "INVALID_ASSIGNMENT", message = exception.Message } });
    }
}).RequireAuthorization();

app.MapPost("/api/tickets/{id}/claim", async (string id, HttpContext context, MySqlStore store, AuthSettings settings, CancellationToken cancellationToken) =>
{
    if (!HasValidCsrf(context, settings)) return Results.Forbid();
    var userId = context.User.FindFirstValue("astra-user-id");
    if (userId is null) return Results.Unauthorized();
    if (!Guid.TryParse(id, out _)) return Results.NotFound();
    try
    {
        var result = await store.ClaimTicket(userId, id, cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }
    catch (UnauthorizedAccessException) { return Results.Forbid(); }
    catch (InvalidOperationException exception) { return Results.Conflict(new { error = new { code = "TICKET_ALREADY_ASSIGNED", message = exception.Message } }); }
}).RequireAuthorization();

app.MapPatch("/api/tickets/{id}", async (string id, UpdateTicketRequest body, HttpContext context, MySqlStore store, AuthSettings settings, CancellationToken cancellationToken) =>
{
    if (!HasValidCsrf(context, settings)) return Results.Forbid();
    var userId = context.User.FindFirstValue("astra-user-id");
    if (userId is null) return Results.Unauthorized();
    if (!Guid.TryParse(id, out _) || body.Status is not (null or "NEW" or "OPEN" or "IN_PROGRESS" or "WAITING_FOR_CLIENT" or "WAITING_FOR_VENDOR" or "ON_HOLD" or "RESOLVED" or "CLOSED" or "CANCELLED") ||
        body.Priority is not (null or "P1_CRITICAL" or "P2_HIGH" or "P3_MEDIUM" or "P4_LOW") || (body.CategoryId is not null && !Guid.TryParse(body.CategoryId, out _)) ||
        (body.TagIds?.Length ?? 0) > 30 || (body.Reason?.Length ?? 0) > 1000 || (body.TagIds ?? []).Any(tag => !Guid.TryParse(tag, out _)))
        return Results.BadRequest(new { error = new { code = "INVALID_TICKET_UPDATE", message = "One or more ticket changes are invalid." } });
    try
    {
        var changed = await store.UpdateTicket(userId, id, body, cancellationToken);
        return changed is null ? Results.NotFound() : Results.Ok(changed);
    }
    catch (UnauthorizedAccessException) { return Results.Forbid(); }
    catch (ArgumentException exception) { return Results.BadRequest(new { error = new { code = "INVALID_TICKET_UPDATE", message = exception.Message } }); }
}).RequireAuthorization();

app.MapPost("/api/tickets/{id}/approval", async (string id, ApprovalRequest body, HttpContext context, MySqlStore store, AuthSettings settings, CancellationToken cancellationToken) =>
{
    if (!HasValidCsrf(context, settings)) return Results.Forbid();
    var userId = context.User.FindFirstValue("astra-user-id");
    if (userId is null) return Results.Unauthorized();
    if (!Guid.TryParse(id, out _) || body.Reason.Trim().Length is < 3 or > 2000)
        return Results.BadRequest(new { error = new { code = "INVALID_APPROVAL", message = "Enter a reason between 3 and 2000 characters." } });
    try
    {
        var result = await store.RequestTicketApproval(userId, id, body, cancellationToken);
        return result is null ? Results.NotFound() : Results.Created($"/api/tickets/{id}", result);
    }
    catch (UnauthorizedAccessException) { return Results.Forbid(); }
    catch (InvalidOperationException exception) { return Results.Conflict(new { error = new { code = "APPROVAL_PENDING", message = exception.Message } }); }
}).RequireAuthorization();

app.MapPost("/api/tickets/{id}/approval/decision", async (string id, ApprovalDecision body, HttpContext context, MySqlStore store, AuthSettings settings, CancellationToken cancellationToken) =>
{
    if (!HasValidCsrf(context, settings)) return Results.Forbid();
    var userId = context.User.FindFirstValue("astra-user-id");
    if (userId is null) return Results.Unauthorized();
    if (!Guid.TryParse(id, out _) || body.Decision is not ("APPROVED" or "REJECTED") || body.Note.Trim().Length is < 2 or > 2000)
        return Results.BadRequest(new { error = new { code = "INVALID_APPROVAL_DECISION", message = "Choose approve or reject and enter a decision note." } });
    try
    {
        var result = await store.DecideTicketApproval(userId, id, body, cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }
    catch (UnauthorizedAccessException) { return Results.Forbid(); }
    catch (InvalidOperationException exception) { return Results.BadRequest(new { error = new { code = "NO_PENDING_APPROVAL", message = exception.Message } }); }
}).RequireAuthorization();

app.MapPost("/api/tickets/{id}/attachments", async (string id, CreateAttachment body, HttpContext context, MySqlStore store, AuthSettings settings, CancellationToken cancellationToken) =>
{
    if (!HasValidCsrf(context, settings)) return Results.Forbid();
    var userId = context.User.FindFirstValue("astra-user-id");
    if (userId is null) return Results.Unauthorized();
    if (!Guid.TryParse(id, out _)) return Results.NotFound();
    try
    {
        var created = await store.AddTicketAttachment(userId, id, body, cancellationToken);
        return created is null ? Results.NotFound() : Results.Created($"/api/tickets/{id}", created);
    }
    catch (UnauthorizedAccessException) { return Results.Forbid(); }
    catch (ArgumentException exception) { return Results.BadRequest(new { error = new { code = "INVALID_ATTACHMENT", message = exception.Message } }); }
}).RequireAuthorization();

app.MapGet("/api/tickets/{id}/attachments/{attachmentId}", async (string id, string attachmentId, HttpContext context, MySqlStore store, CancellationToken cancellationToken) =>
{
    var userId = context.User.FindFirstValue("astra-user-id");
    if (userId is null) return Results.Unauthorized();
    if (!Guid.TryParse(id, out _) || !Guid.TryParse(attachmentId, out _)) return Results.NotFound();
    var file = await store.GetTicketAttachment(userId, id, attachmentId, cancellationToken);
    if (file is null) return Results.NotFound();
    context.Response.Headers.XContentTypeOptions = "nosniff";
    return Results.File(file.Content, file.ContentType, file.FileName);
}).RequireAuthorization();

app.MapFeatureApis(HasValidCsrf);
app.MapFallback("/api/{**path}", () => FeatureApis.Error(404, "API_NOT_FOUND", "The requested API endpoint was not found."));
app.Run();

static string SafeReturnTo(string? value) => value is "/client" or "/operations" or "/management" or "/admin" ? value : "/";

static bool IsLocalPreviewRequest(HttpContext context, AuthSettings settings)
{
    if (context.Connection.RemoteIpAddress is not { } address || !System.Net.IPAddress.IsLoopback(address)) return false;
    return IsAllowedLocalPreviewOrigin(context, settings, $"{context.Request.Scheme}://{context.Request.Host}");
}

static bool IsAllowedLocalPreviewOrigin(HttpContext context, AuthSettings settings, string candidate)
{
    var additional = context.RequestServices.GetRequiredService<IConfiguration>()["ASTRA_LOCAL_PREVIEW_ORIGINS"] ?? "";
    return additional.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Append(settings.ApplicationBaseUrl).Any(value =>
            Uri.TryCreate(value, UriKind.Absolute, out var origin) && origin.IsLoopback &&
            string.Equals(candidate, origin.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase));
}

static string SessionApplicationBaseUrl(HttpContext context, AuthSettings settings)
{
    if (!context.User.HasClaim("astra-local-preview", "true") || !IsLocalPreviewRequest(context, settings)) return settings.ApplicationBaseUrl;
    var browserSource = context.Request.Headers.Origin.FirstOrDefault() ?? context.Request.Headers.Referer.FirstOrDefault();
    if (Uri.TryCreate(browserSource, UriKind.Absolute, out var source) &&
        IsAllowedLocalPreviewOrigin(context, settings, source.GetLeftPart(UriPartial.Authority)))
        return source.GetLeftPart(UriPartial.Authority);
    return $"{context.Request.Scheme}://{context.Request.Host}";
}

static bool HasValidCsrf(HttpContext context, AuthSettings settings)
{
    if (!Uri.TryCreate(SessionApplicationBaseUrl(context, settings), UriKind.Absolute, out var appUri) ||
        !string.Equals(context.Request.Headers.Origin.ToString(), appUri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase)) return false;
    var supplied = context.Request.Headers["x-csrf-token"].ToString();
    var expected = context.User.FindFirstValue("astra-csrf");
    return expected is not null && supplied.Length == expected.Length &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(expected));
}

static string ResolveWorkspace(string referer, string scheme, string requestHost, string appBaseUrl)
{
    if (!Uri.TryCreate(appBaseUrl, UriKind.Absolute, out var expectedOrigin)) return "default";
    var expected = expectedOrigin.GetLeftPart(UriPartial.Authority);
    if (!Uri.TryCreate(referer, UriKind.Absolute, out var source) ||
        !string.Equals(source.GetLeftPart(UriPartial.Authority), expected, StringComparison.OrdinalIgnoreCase) ||
        !string.Equals($"{scheme}://{requestHost}", expected, StringComparison.OrdinalIgnoreCase)) return "default";
    var path = source.AbsolutePath;
    if (new[] { "/changes", "/problems", "/knowledge", "/catalogue", "/help", "/service-reports" }
        .Any(prefix => path == prefix || path.StartsWith(prefix + "/", StringComparison.Ordinal)))
    {
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(source.Query);
        var selected = query["workspace"].FirstOrDefault();
        if (selected is "admin" or "management" or "operations" or "client") return selected;
    }
    if (path.StartsWith("/admin", StringComparison.Ordinal)) return "admin";
    if (path.StartsWith("/management", StringComparison.Ordinal)) return "management";
    if (path.StartsWith("/operations", StringComparison.Ordinal)) return "operations";
    if (path.StartsWith("/client", StringComparison.Ordinal)) return "client";
    return path == "/" ? "chooser" : "default";
}

public sealed record AuthSettings(string ClientId, string ClientSecret, string TenantId, string Authority, string Issuer, string ApplicationBaseUrl, bool Configured)
{
    public static AuthSettings Load(IConfiguration config)
    {
        string? fileClientId = null, fileSecret = null, fileTenant = null;
        var privateSettingsPath = config["ASTRA_PRIVATE_RUNTIME_SETTINGS_FILE"];
        if (!string.IsNullOrWhiteSpace(privateSettingsPath))
        {
            try
            {
                var path = Path.GetFullPath(privateSettingsPath);
                if (!OperatingSystem.IsWindows())
                {
                    var permissions = File.GetUnixFileMode(path);
                    var forbidden = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                                    UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
                    if ((permissions & forbidden) != 0) throw new InvalidOperationException("Private settings file permissions are too broad.");
                }
                using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                var root = document.RootElement;
                if (root.GetProperty("format").GetInt32() != 1) throw new InvalidOperationException("Unsupported private settings format.");
                var entra = root.GetProperty("entra");
                fileClientId = entra.GetProperty("clientId").GetString();
                fileSecret = entra.GetProperty("clientSecret").GetString();
                fileTenant = entra.GetProperty("tenantId").GetString();
            }
            catch
            {
                // Authentication remains disabled. Never surface the file, its values, or the parse error.
            }
        }
        var clientId = config["ENTRA_CLIENT_ID"] ?? fileClientId ?? "";
        var secret = config["ENTRA_CLIENT_SECRET"] ?? fileSecret ?? "";
        var tenant = config["ENTRA_TENANT_ID"] ?? fileTenant ?? "";
        var credentialsValid = Guid.TryParse(clientId, out _) && Guid.TryParse(tenant, out _) && secret.Length >= 16;
        var callbackReady = string.Equals(config["ASTRA_AUTH_CALLBACK_READY"], "true", StringComparison.OrdinalIgnoreCase);
        var configured = credentialsValid && callbackReady;
        var normalizedTenant = Guid.TryParse(tenant, out var tenantId) ? tenantId.ToString("D") : "common";
        var authority = $"https://login.microsoftonline.com/{normalizedTenant}/v2.0";
        return new AuthSettings(clientId, secret, tenant, authority,
            $"https://login.microsoftonline.com/{normalizedTenant}/v2.0", config["APP_BASE_URL"] ?? "http://localhost:3200", configured);
    }
}

public sealed partial class MySqlStore(IConfiguration configuration, IMailTransport? mailTransport = null, IMailInbox? mailInbox = null, IIdentityDirectory? identityDirectory = null)
{
    public async Task<KnownAccount> EnsureLocalPreviewAccount(CancellationToken ct)
    {
        const string id = "43d108e7-a710-4580-bf72-a624240dc352";
        const string tenant = "00000000-0000-0000-0000-000000000000";
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var command = new MySqlCommand("INSERT INTO users(id,entra_tenant_id,entra_object_id,email,display_name,work_scope,change_access) SELECT @id,@tenant,@id,'local-preview@example.invalid','Local Test Administrator','ALL','CHANGE_MANAGER' WHERE NOT EXISTS(SELECT 1 FROM users WHERE id=@id)", connection, transaction))
        {
            command.Parameters.AddWithValue("@id", id);
            command.Parameters.AddWithValue("@tenant", tenant);
            await command.ExecuteNonQueryAsync(ct);
        }
        await using var verify = new MySqlCommand("SELECT session_version FROM users WHERE id=@id AND entra_tenant_id=@tenant AND entra_object_id=@id AND email='local-preview@example.invalid' AND active=1 AND (access_expires_at IS NULL OR access_expires_at>UTC_TIMESTAMP(6)) FOR UPDATE", connection, transaction);
        verify.Parameters.AddWithValue("@id", id);
        verify.Parameters.AddWithValue("@tenant", tenant);
        var version = await verify.ExecuteScalarAsync(ct);
        if (version is null) throw new InvalidOperationException("The local test account is unavailable.");
        await using (var role = new MySqlCommand("INSERT INTO roles(code,name,description) VALUES('ADMIN','Administrator','Application administrator') ON DUPLICATE KEY UPDATE code=code", connection, transaction))
            await role.ExecuteNonQueryAsync(ct);
        await using (var grant = new MySqlCommand("INSERT IGNORE INTO user_roles(user_id,role_code) VALUES(@id,'ADMIN')", connection, transaction))
        {
            grant.Parameters.AddWithValue("@id", id);
            await grant.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return new KnownAccount(id, Convert.ToInt32(version));
    }

    private string? ConnectionString => configuration.GetConnectionString("MySql");
    private MySqlConnection CreateConnection()
    {
        var builder = new MySqlConnectionStringBuilder(ConnectionString ?? throw new InvalidOperationException("Database is not configured."))
        {
            // The migrated schema stores UUIDs as CHAR(36), so expose them as strings.
            GuidFormat = MySqlGuidFormat.None
        };
        return new MySqlConnection(builder.ConnectionString);
    }

    public async Task<bool> IsAdministrator(string userId, CancellationToken ct)
    {
        if (ConnectionString is null) return false;
        await using var connection = CreateConnection(); await connection.OpenAsync(ct);
        await using var command = new MySqlCommand("SELECT EXISTS(SELECT 1 FROM user_roles ur JOIN users u ON u.id=ur.user_id WHERE ur.user_id=@id AND ur.role_code='ADMIN' AND u.active=1 AND (u.access_expires_at IS NULL OR u.access_expires_at>UTC_TIMESTAMP(6)))", connection); command.Parameters.AddWithValue("@id", userId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private async Task<bool> HasPermission(string userId,string permission,IReadOnlySet<string>? defaultRoles,CancellationToken ct)
    {
        if(ConnectionString is null)return false;
        await using var connection=CreateConnection();await connection.OpenAsync(ct);
        var roleAllowed=false;
        const string roleSql="SELECT ur.role_code,rp.permissions FROM user_roles ur LEFT JOIN role_access_policies rp ON rp.role_code=ur.role_code WHERE ur.user_id=@id";
        await using(var roles=new MySqlCommand(roleSql,connection)){roles.Parameters.AddWithValue("@id",userId);await using var roleReader=await roles.ExecuteReaderAsync(ct);while(await roleReader.ReadAsync(ct)){var role=roleReader.GetString(0);if(!roleReader.IsDBNull(1)){try{var permissions=System.Text.Json.JsonSerializer.Deserialize<string[]>(roleReader.GetString(1))??[];if(permissions.Contains(permission,StringComparer.Ordinal))roleAllowed=true;}catch(System.Text.Json.JsonException){}}else if(defaultRoles?.Contains(role)==true)roleAllowed=true;}}
        const string extraSql="SELECT EXISTS(SELECT 1 FROM user_permissions WHERE user_id=@id AND permission_code=@permission),EXISTS(SELECT 1 FROM user_access_policies WHERE user_id=@id AND JSON_CONTAINS(allow_permissions,JSON_QUOTE(@permission))),EXISTS(SELECT 1 FROM staff_group_members gm JOIN access_groups g ON g.id=gm.group_id AND g.kind='STAFF' AND g.active=1 WHERE gm.user_id=@id AND JSON_CONTAINS(g.permissions,JSON_QUOTE(@permission))),EXISTS(SELECT 1 FROM user_access_policies WHERE user_id=@id AND JSON_CONTAINS(deny_permissions,JSON_QUOTE(@permission)))";
        await using var command=new MySqlCommand(extraSql,connection);command.Parameters.AddWithValue("@id",userId);command.Parameters.AddWithValue("@permission",permission);await using var reader=await command.ExecuteReaderAsync(ct);if(!await reader.ReadAsync(ct))return false;var allowed=roleAllowed||reader.GetBoolean(0)||reader.GetBoolean(1)||reader.GetBoolean(2);return allowed&&!reader.GetBoolean(3);
    }

    public async Task<CreatedContact?> CreateContact(string actorId,CreateContactRequest body,CancellationToken ct)
    {
        if(ConnectionString is null)return null;if(!await IsAdministrator(actorId,ct))throw new UnauthorizedAccessException();
        var name=body.Name.Trim();var email=body.Email?.Trim();var phone=body.Phone?.Trim();
        if(!Guid.TryParse(body.ClientId,out _)||name.Length is <1 or >200||(email?.Length??0)>320||(phone?.Length??0)>50)throw new ArgumentException("Enter a valid client and contact name.");
        if(!string.IsNullOrEmpty(email)){try{_=new System.Net.Mail.MailAddress(email);}catch(FormatException){throw new ArgumentException("Enter a valid email address.");}}
        await using var connection=CreateConnection();await connection.OpenAsync(ct);await using var transaction=await connection.BeginTransactionAsync(ct);
        try{
            await using var client=new MySqlCommand("SELECT 1 FROM clients WHERE id=@client AND status='ACTIVE'",connection,transaction);client.Parameters.AddWithValue("@client",body.ClientId);if(await client.ExecuteScalarAsync(ct)is null)throw new ArgumentException("The selected client is unavailable.");
            if(body.SiteId is not null){await using var site=new MySqlCommand("SELECT 1 FROM sites WHERE id=@site AND client_id=@client AND active=1",connection,transaction);site.Parameters.AddWithValue("@site",body.SiteId);site.Parameters.AddWithValue("@client",body.ClientId);if(await site.ExecuteScalarAsync(ct)is null)throw new ArgumentException("The selected site does not belong to this client.");}
            var id=Guid.NewGuid().ToString("D");await using(var insert=new MySqlCommand("INSERT INTO contacts(id,client_id,site_id,name,email,phone,active) VALUES(@id,@client,@site,@name,@email,@phone,1)",connection,transaction)){insert.Parameters.AddWithValue("@id",id);insert.Parameters.AddWithValue("@client",body.ClientId);insert.Parameters.AddWithValue("@site",(object?)body.SiteId??DBNull.Value);insert.Parameters.AddWithValue("@name",name);insert.Parameters.AddWithValue("@email",(object?)email??DBNull.Value);insert.Parameters.AddWithValue("@phone",(object?)phone??DBNull.Value);await insert.ExecuteNonQueryAsync(ct);}
            await AddAudit(connection,transaction,actorId,"CONTACT.CREATED","CONTACT",id,new{clientId=body.ClientId,siteId=body.SiteId});await transaction.CommitAsync(ct);return new CreatedContact(id);
        }catch{await transaction.RollbackAsync(ct);throw;}
    }

    public async Task<CreatedEntity?> CreateClient(string actorId,CreateClientRequest body,CancellationToken ct)
    {
        if(ConnectionString is null)return null;if(!await IsAdministrator(actorId,ct))throw new UnauthorizedAccessException();var name=body.Name.Trim();var email=body.PrimaryEmail?.Trim();var phone=body.PrimaryPhone?.Trim();var notes=body.Notes?.Trim();
        if(name.Length is <1 or >200||(email?.Length??0)>320||(phone?.Length??0)>50||(notes?.Length??0)>4000)throw new ArgumentException("Enter valid client details.");
        if(!string.IsNullOrEmpty(email)){try{_=new System.Net.Mail.MailAddress(email);}catch(FormatException){throw new ArgumentException("Enter a valid primary email address.");}}
        var requested=body.TicketPrefix?.Trim().ToUpperInvariant();if(requested is not null&&!System.Text.RegularExpressions.Regex.IsMatch(requested,"^[A-Z][A-Z0-9]{1,7}$"))throw new ArgumentException("The client ticket prefix must be 2 to 8 uppercase letters or numbers.");
        await using var connection=CreateConnection();await connection.OpenAsync(ct);await using var transaction=await connection.BeginTransactionAsync(ct);
        try{
            var basePrefix=requested??new string(name.Where(char.IsLetterOrDigit).Take(3).ToArray()).ToUpperInvariant();if(basePrefix.Length==0)basePrefix="CLT";if(basePrefix.Length==1)basePrefix+="X";if(basePrefix.Length>6)basePrefix=basePrefix[..6];var prefix=basePrefix;
            if(requested is null){var suffix=2;while(true){await using var reserved=new MySqlCommand("SELECT 1 FROM ticket_prefix_registry WHERE prefix=@prefix",connection,transaction);reserved.Parameters.AddWithValue("@prefix",prefix);if(await reserved.ExecuteScalarAsync(ct)is null)break;prefix=basePrefix+suffix++.ToString(System.Globalization.CultureInfo.InvariantCulture);if(prefix.Length>8)throw new ArgumentException("Choose a shorter name or provide a ticket prefix.");}}
            var id=Guid.NewGuid().ToString("D");await using(var insert=new MySqlCommand("INSERT INTO clients(id,name,ticket_prefix,primary_email,primary_phone,notes,status) VALUES(@id,@name,@prefix,@email,@phone,@notes,'ACTIVE')",connection,transaction)){insert.Parameters.AddWithValue("@id",id);insert.Parameters.AddWithValue("@name",name);insert.Parameters.AddWithValue("@prefix",prefix);insert.Parameters.AddWithValue("@email",(object?)email??DBNull.Value);insert.Parameters.AddWithValue("@phone",(object?)phone??DBNull.Value);insert.Parameters.AddWithValue("@notes",(object?)notes??DBNull.Value);await insert.ExecuteNonQueryAsync(ct);}
            await using(var sequence=new MySqlCommand("INSERT INTO client_ticket_sequences(client_id,next_number) VALUES(@id,1)",connection,transaction)){sequence.Parameters.AddWithValue("@id",id);await sequence.ExecuteNonQueryAsync(ct);}
            await AddAudit(connection,transaction,actorId,"CLIENT.CREATED","CLIENT",id,new{name,ticketPrefix=prefix});await transaction.CommitAsync(ct);return new CreatedEntity(id);
        }catch(MySqlException ex)when(ex.Number==1062){await transaction.RollbackAsync(ct);throw new ArgumentException("That client name or ticket prefix is already in use.");}catch{await transaction.RollbackAsync(ct);throw;}
    }

    public async Task<CreatedEntity?> CreateAsset(string actorId,CreateAssetRequest body,CancellationToken ct)
    {
        if(ConnectionString is null)return null;if(!await IsAdministrator(actorId,ct))throw new UnauthorizedAccessException();var name=body.Name.Trim();var type=body.AssetType.Trim();
        if(!Guid.TryParse(body.ClientId,out _)||name.Length is <1 or >200||type.Length is <1 or >100||(body.SerialNumber?.Length??0)>200||(body.ExternalReference?.Length??0)>500||(body.SiteId is not null&&!Guid.TryParse(body.SiteId,out _)))throw new ArgumentException("Enter valid asset details.");
        await using var connection=CreateConnection();await connection.OpenAsync(ct);await using var transaction=await connection.BeginTransactionAsync(ct);
        try{await using var client=new MySqlCommand("SELECT 1 FROM clients WHERE id=@id AND status='ACTIVE'",connection,transaction);client.Parameters.AddWithValue("@id",body.ClientId);if(await client.ExecuteScalarAsync(ct)is null)throw new ArgumentException("The selected client is unavailable.");if(body.SiteId is not null){await using var site=new MySqlCommand("SELECT 1 FROM sites WHERE id=@site AND client_id=@client AND active=1",connection,transaction);site.Parameters.AddWithValue("@site",body.SiteId);site.Parameters.AddWithValue("@client",body.ClientId);if(await site.ExecuteScalarAsync(ct)is null)throw new ArgumentException("The selected site does not belong to this client.");}
            var id=Guid.NewGuid().ToString("D");await using(var insert=new MySqlCommand("INSERT INTO assets(id,client_id,site_id,name,asset_type,serial_number,external_reference,active) VALUES(@id,@client,@site,@name,@type,@serial,@external,1)",connection,transaction)){insert.Parameters.AddWithValue("@id",id);insert.Parameters.AddWithValue("@client",body.ClientId);insert.Parameters.AddWithValue("@site",(object?)body.SiteId??DBNull.Value);insert.Parameters.AddWithValue("@name",name);insert.Parameters.AddWithValue("@type",type);insert.Parameters.AddWithValue("@serial",(object?)body.SerialNumber??DBNull.Value);insert.Parameters.AddWithValue("@external",(object?)body.ExternalReference??DBNull.Value);await insert.ExecuteNonQueryAsync(ct);}
            await AddAudit(connection,transaction,actorId,"ASSET.CREATED","ASSET",id,new{clientId=body.ClientId});await transaction.CommitAsync(ct);return new CreatedEntity(id);
        }catch{await transaction.RollbackAsync(ct);throw;}
    }

    public async Task<CreatedEntity?> CreateSite(string actorId,CreateSiteRequest body,CancellationToken ct)
    {
        if(ConnectionString is null)return null;if(!await IsAdministrator(actorId,ct))throw new UnauthorizedAccessException();var name=body.Name.Trim();var address=body.Address?.Trim();
        if(!Guid.TryParse(body.ClientId,out _)||name.Length is <1 or >200||(address?.Length??0)>2000)throw new ArgumentException("Enter valid site details.");var prefix=body.TicketPrefix?.Trim().ToUpperInvariant();if(prefix is not null&&!System.Text.RegularExpressions.Regex.IsMatch(prefix,"^[A-Z][A-Z0-9]{1,15}$"))throw new ArgumentException("The site ticket prefix must be 2 to 16 uppercase letters or numbers.");
        await using var connection=CreateConnection();await connection.OpenAsync(ct);await using var transaction=await connection.BeginTransactionAsync(ct);
        try{await using var client=new MySqlCommand("SELECT 1 FROM clients WHERE id=@id AND status='ACTIVE' FOR UPDATE",connection,transaction);client.Parameters.AddWithValue("@id",body.ClientId);if(await client.ExecuteScalarAsync(ct)is null)throw new ArgumentException("The selected client is unavailable.");
            var id=Guid.NewGuid().ToString("D");await using(var insert=new MySqlCommand("INSERT INTO sites(id,client_id,name,address,ticket_prefix,active) VALUES(@id,@client,@name,@address,@prefix,1)",connection,transaction)){insert.Parameters.AddWithValue("@id",id);insert.Parameters.AddWithValue("@client",body.ClientId);insert.Parameters.AddWithValue("@name",name);insert.Parameters.AddWithValue("@address",(object?)address??DBNull.Value);insert.Parameters.AddWithValue("@prefix",(object?)prefix??DBNull.Value);await insert.ExecuteNonQueryAsync(ct);}
            await AddAudit(connection,transaction,actorId,"SITE.CREATED","SITE",id,new{clientId=body.ClientId});await transaction.CommitAsync(ct);return new CreatedEntity(id);
        }catch(MySqlException ex)when(ex.Number==1062){await transaction.RollbackAsync(ct);throw new ArgumentException("That site name or ticket prefix is already in use.");}catch{await transaction.RollbackAsync(ct);throw;}
    }

    public async Task<CreatedEntity?> CreateCategory(string actorId,CreateCategoryRequest body,CancellationToken ct)
    {
        if(ConnectionString is null)return null;if(!await HasPermission(actorId,"CATEGORY_MANAGE",new HashSet<string>(["TEAM_LEAD","MANAGER","ADMIN"],StringComparer.Ordinal),ct))throw new UnauthorizedAccessException();
        var name=body.Name.Trim();if(name.Length is <1 or >200||(body.Description?.Length??0)>2000||(body.ParentId is not null&&!Guid.TryParse(body.ParentId,out _)))throw new ArgumentException("Enter valid category details.");
        await using var connection=CreateConnection();await connection.OpenAsync(ct);await using var transaction=await connection.BeginTransactionAsync(ct);
        try{if(body.ParentId is not null){await using var parent=new MySqlCommand("SELECT 1 FROM categories WHERE id=@id AND active=1",connection,transaction);parent.Parameters.AddWithValue("@id",body.ParentId);if(await parent.ExecuteScalarAsync(ct)is null)throw new ArgumentException("The parent category is unavailable.");}
            var id=Guid.NewGuid().ToString("D");await using(var insert=new MySqlCommand("INSERT INTO categories(id,parent_id,name,description,active) VALUES(@id,@parent,@name,@description,1)",connection,transaction)){insert.Parameters.AddWithValue("@id",id);insert.Parameters.AddWithValue("@parent",(object?)body.ParentId??DBNull.Value);insert.Parameters.AddWithValue("@name",name);insert.Parameters.AddWithValue("@description",(object?)body.Description??DBNull.Value);await insert.ExecuteNonQueryAsync(ct);}
            await AddAudit(connection,transaction,actorId,"CATEGORY.CREATED","CATEGORY",id,new{name});await transaction.CommitAsync(ct);return new CreatedEntity(id);
        }catch{await transaction.RollbackAsync(ct);throw;}
    }

    public async Task<CreatedEntity?> CreateTag(string actorId,CreateTagRequest body,CancellationToken ct)
    {
        if(ConnectionString is null)return null;if(!await IsAdministrator(actorId,ct))throw new UnauthorizedAccessException();var name=body.Name.Trim();if(name.Length is <1 or >100||(body.Description?.Length??0)>1000)throw new ArgumentException("Enter valid tag details.");
        await using var connection=CreateConnection();await connection.OpenAsync(ct);var id=Guid.NewGuid().ToString("D");await using var transaction=await connection.BeginTransactionAsync(ct);
        try{await using(var insert=new MySqlCommand("INSERT INTO tags(id,name,description,active) VALUES(@id,@name,@description,1)",connection,transaction)){insert.Parameters.AddWithValue("@id",id);insert.Parameters.AddWithValue("@name",name);insert.Parameters.AddWithValue("@description",(object?)body.Description??DBNull.Value);await insert.ExecuteNonQueryAsync(ct);}await AddAudit(connection,transaction,actorId,"TAG.CREATED","TAG",id,new{name});await transaction.CommitAsync(ct);return new CreatedEntity(id);}catch{await transaction.RollbackAsync(ct);throw;}
    }

    public async Task<TicketSourceView?> CreateTicketSource(string actorId,TicketSourceRequest body,CancellationToken ct)
    {
        if(ConnectionString is null)return null;if(!await HasPermission(actorId,"SOURCES_MANAGE",new HashSet<string>(["ADMIN"],StringComparer.Ordinal),ct))throw new UnauthorizedAccessException();var code=body.Code.Trim();var name=body.Name.Trim();if(!System.Text.RegularExpressions.Regex.IsMatch(code,"^[A-Z0-9_]{1,50}$")||name.Length is <1 or >100)throw new ArgumentException("Use an uppercase source code and a source name.");
        await using var connection=CreateConnection();await connection.OpenAsync(ct);await using var transaction=await connection.BeginTransactionAsync(ct);
        try{await using(var insert=new MySqlCommand("INSERT INTO ticket_sources(code,name,active) VALUES(@code,@name,1)",connection,transaction)){insert.Parameters.AddWithValue("@code",code);insert.Parameters.AddWithValue("@name",name);await insert.ExecuteNonQueryAsync(ct);}await AddAudit(connection,transaction,actorId,"TICKET_SOURCE.CREATED","TICKET_SOURCE",code,new{name});await transaction.CommitAsync(ct);return new TicketSourceView(code,name,true,100);}catch(MySqlException ex)when(ex.Number==1062){await transaction.RollbackAsync(ct);throw new ArgumentException("A ticket source with this code already exists.");}catch{await transaction.RollbackAsync(ct);throw;}
    }

    public async Task<TicketSourceView?> UpdateTicketSource(string actorId,string code,UpdateTicketSourceRequest body,CancellationToken ct)
    {
        if(ConnectionString is null)return null;if(!await HasPermission(actorId,"SOURCES_MANAGE",new HashSet<string>(["ADMIN"],StringComparer.Ordinal),ct))throw new UnauthorizedAccessException();if(string.IsNullOrWhiteSpace(code)||body.Name.Trim().Length is <1 or >100)throw new ArgumentException("Enter a valid ticket source name.");
        await using var connection=CreateConnection();await connection.OpenAsync(ct);await using var transaction=await connection.BeginTransactionAsync(ct);
        try{await using var update=new MySqlCommand("UPDATE ticket_sources SET name=@name,active=@active,updated_at=UTC_TIMESTAMP(6) WHERE code=@code",connection,transaction);update.Parameters.AddWithValue("@name",body.Name.Trim());update.Parameters.AddWithValue("@active",body.Active);update.Parameters.AddWithValue("@code",code);if(await update.ExecuteNonQueryAsync(ct)==0)return null;await AddAudit(connection,transaction,actorId,"TICKET_SOURCE.UPDATED","TICKET_SOURCE",code,new{name=body.Name.Trim(),active=body.Active});await transaction.CommitAsync(ct);return new TicketSourceView(code,body.Name.Trim(),body.Active,100);}catch{await transaction.RollbackAsync(ct);throw;}
    }

    private static async Task AddAudit(MySqlConnection connection,MySqlTransaction transaction,string actor,string action,string entityType,string? entityId,object details,CancellationToken ct=default)
    {
        await using var command=new MySqlCommand("INSERT INTO audit_logs(actor_user_id,action,entity_type,entity_id,details) VALUES(@actor,@action,@type,@entity,CAST(@details AS JSON))",connection,transaction);command.Parameters.AddWithValue("@actor",actor);command.Parameters.AddWithValue("@action",action);command.Parameters.AddWithValue("@type",entityType);command.Parameters.AddWithValue("@entity",(object?)entityId??DBNull.Value);command.Parameters.AddWithValue("@details",System.Text.Json.JsonSerializer.Serialize(details));await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<Dictionary<string, object?>>> GetRoles(CancellationToken ct)
    {
        if (ConnectionString is null) return [];
        await using var connection = CreateConnection(); await connection.OpenAsync(ct);
        await using var command = new MySqlCommand("SELECT code,name,description FROM roles ORDER BY code", connection);
        await using var reader = await command.ExecuteReaderAsync(ct); var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync(ct)) { var row = new Dictionary<string, object?>(StringComparer.Ordinal); for (var i=0;i<reader.FieldCount;i++) row[reader.GetName(i)]=ReadValue(reader,i); rows.Add(row); }
        return rows;
    }

    public async Task<List<Dictionary<string, object?>>> GetPermissions(CancellationToken ct)
    {
        if (ConnectionString is null) return [];
        await using var connection = CreateConnection(); await connection.OpenAsync(ct);
        await using var command = new MySqlCommand("SELECT code,name,description FROM permissions ORDER BY name", connection);
        await using var reader = await command.ExecuteReaderAsync(ct); var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync(ct)) { var row = new Dictionary<string, object?>(StringComparer.Ordinal); for (var i=0;i<reader.FieldCount;i++) row[reader.GetName(i)]=ReadValue(reader,i); rows.Add(row); }
        return rows;
    }

    public async Task<List<Dictionary<string, object?>>> GetTicketSources(CancellationToken ct)
    {
        if (ConnectionString is null) return [];
        await using var connection = CreateConnection(); await connection.OpenAsync(ct);
        await using var command = new MySqlCommand("SELECT code,name,active,sort_order FROM ticket_sources ORDER BY sort_order,name", connection);
        await using var reader = await command.ExecuteReaderAsync(ct); var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync(ct)) { var row = new Dictionary<string, object?>(StringComparer.Ordinal); for (var i=0;i<reader.FieldCount;i++) row[reader.GetName(i)]=ReadValue(reader,i); rows.Add(row); }
        return rows;
    }

    public async Task<List<Dictionary<string, object?>>?> ListSlaSettings(string userId, string kind, CancellationToken ct)
    {
        if (ConnectionString is null || kind is not ("policies" or "calendars")) return null;
        if (!await IsAdministrator(userId, ct) && !await HasPermission(userId, "SLA_MANAGE", new HashSet<string>(["MANAGER", "ADMIN"], StringComparer.Ordinal), ct)) return null;
        var sql = kind == "policies"
            ? "SELECT p.id,p.priority,p.response_minutes,p.resolution_minutes,p.warning_percent,p.active,p.calendar_id,c.name AS calendar_name,p.updated_at FROM sla_policies p JOIN sla_calendars c ON c.id=p.calendar_id ORDER BY CASE p.priority WHEN 'P1_CRITICAL' THEN 1 WHEN 'P2_HIGH' THEN 2 WHEN 'P3_MEDIUM' THEN 3 ELSE 4 END"
            : "SELECT c.id,c.name,c.timezone,c.working_days,c.work_start,c.work_end,c.active,(SELECT COALESCE(JSON_ARRAYAGG(JSON_OBJECT('id',h.id,'date',DATE_FORMAT(h.holiday_date,'%Y-%m-%d'),'name',h.name)),JSON_ARRAY()) FROM sla_holidays h WHERE h.calendar_id=c.id) AS holidays FROM sla_calendars c ORDER BY c.name";
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        await using var command = new MySqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var key = reader.GetName(i);
                var value = ReadValue(reader, i);
                if ((key is "working_days" or "holidays") && value is string json)
                    value = System.Text.Json.JsonDocument.Parse(json).RootElement.Clone();
                row[key] = value;
            }
            rows.Add(row);
        }
        return rows;
    }

    public async Task<CreatedEntity?> SaveSlaCalendar(string userId, string? id, SlaCalendarRequest body, CancellationToken ct)
    {
        if (ConnectionString is null || !await CanManageSla(userId, ct)) return null;
        var name = body.Name.Trim();
        if (name.Length is < 2 or > 120 || body.Timezone.Length is < 1 or > 80 || body.WorkingDays is null || body.WorkingDays.Count is < 1 or > 7 || body.WorkingDays.Distinct().Count() != body.WorkingDays.Count || body.WorkingDays.Any(day => day is < 1 or > 7) || body.Holidays is null || body.Holidays.Count > 100)
            throw new ArgumentException("Enter a valid calendar name, timezone, working days, and holidays.");
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(body.Timezone); }
        catch (TimeZoneNotFoundException) { throw new ArgumentException("Use a valid timezone."); }
        if (!TimeSpan.TryParseExact(body.WorkStart, @"hh\:mm", System.Globalization.CultureInfo.InvariantCulture, out var start) || !TimeSpan.TryParseExact(body.WorkEnd, @"hh\:mm", System.Globalization.CultureInfo.InvariantCulture, out var end) || start >= end)
            throw new ArgumentException("Calendar end time must be after start time.");
        var holidays = new List<(DateOnly Date, string Name)>();
        foreach (var holiday in body.Holidays)
        {
            if (!DateOnly.TryParseExact(holiday.Date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date) || string.IsNullOrWhiteSpace(holiday.Name) || holiday.Name.Trim().Length > 120)
                throw new ArgumentException("Enter a valid holiday date and name.");
            holidays.Add((date, holiday.Name.Trim()));
        }
        if (holidays.Select(holiday => holiday.Date).Distinct().Count() != holidays.Count) throw new ArgumentException("A holiday date appears more than once.");
        var calendarId = id ?? Guid.NewGuid().ToString("D");
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            if (id is not null)
            {
                await using var existing = new MySqlCommand("SELECT active FROM sla_calendars WHERE id=@id FOR UPDATE", connection, transaction);
                existing.Parameters.AddWithValue("@id", calendarId);
                var exists = await existing.ExecuteScalarAsync(ct);
                if (exists is null) throw new KeyNotFoundException();
                if (!body.Active)
                {
                    await using var used = new MySqlCommand("SELECT EXISTS(SELECT 1 FROM sla_policies WHERE calendar_id=@id AND active=1 UNION ALL SELECT 1 FROM ticket_sla s JOIN tickets t ON t.id=s.ticket_id WHERE s.calendar_id=@id AND t.status NOT IN ('RESOLVED','CLOSED','CANCELLED'))", connection, transaction);
                    used.Parameters.AddWithValue("@id", calendarId);
                    if (Convert.ToInt32(await used.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture) == 1)
                        throw new ArgumentException("An active SLA policy or ticket is using this calendar.");
                }
            }
            var sql = id is null
                ? "INSERT INTO sla_calendars(id,name,timezone,working_days,work_start,work_end,active,updated_by_user_id) VALUES(@id,@name,@zone,@days,@start,@end,@active,@actor)"
                : "UPDATE sla_calendars SET name=@name,timezone=@zone,working_days=@days,work_start=@start,work_end=@end,active=@active,updated_by_user_id=@actor,updated_at=UTC_TIMESTAMP(6) WHERE id=@id";
            await using (var save = new MySqlCommand(sql, connection, transaction))
            {
                save.Parameters.AddWithValue("@id", calendarId);
                save.Parameters.AddWithValue("@name", name);
                save.Parameters.AddWithValue("@zone", body.Timezone);
                save.Parameters.AddWithValue("@days", System.Text.Json.JsonSerializer.Serialize(body.WorkingDays));
                save.Parameters.AddWithValue("@start", start);
                save.Parameters.AddWithValue("@end", end);
                save.Parameters.AddWithValue("@active", body.Active);
                save.Parameters.AddWithValue("@actor", userId);
                await save.ExecuteNonQueryAsync(ct);
            }
            if (id is not null)
            {
                await using var delete = new MySqlCommand("DELETE FROM sla_holidays WHERE calendar_id=@id", connection, transaction);
                delete.Parameters.AddWithValue("@id", calendarId);
                await delete.ExecuteNonQueryAsync(ct);
            }
            foreach (var holiday in holidays)
            {
                await using var insert = new MySqlCommand("INSERT INTO sla_holidays(id,calendar_id,holiday_date,name) VALUES(@id,@calendar,@date,@name)", connection, transaction);
                insert.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("D"));
                insert.Parameters.AddWithValue("@calendar", calendarId);
                insert.Parameters.AddWithValue("@date", holiday.Date.ToDateTime(TimeOnly.MinValue));
                insert.Parameters.AddWithValue("@name", holiday.Name);
                await insert.ExecuteNonQueryAsync(ct);
            }
            await AddAudit(connection, transaction, userId, id is null ? "SLA_CALENDAR.CREATED" : "SLA_CALENDAR.UPDATED", "SLA_CALENDAR", calendarId, new { name }, ct);
            await transaction.CommitAsync(ct);
            return new CreatedEntity(calendarId);
        }
        catch (MySqlException exception) when (exception.Number == 1062)
        {
            await transaction.RollbackAsync(ct);
            throw new ArgumentException("A calendar with this name already exists.");
        }
        catch { await transaction.RollbackAsync(ct); throw; }
    }

    public async Task<object?> SaveSlaPolicy(string actorId, string priority, SlaPolicyRequest body, CancellationToken ct)
    {
        if (ConnectionString is null || !await CanManageSla(actorId, ct)) return null;
        if (priority is not ("P1_CRITICAL" or "P2_HIGH" or "P3_MEDIUM" or "P4_LOW") || body.ResponseMinutes is < 1 or > 525600 || body.ResolutionMinutes is < 1 or > 525600 || body.ResolutionMinutes < body.ResponseMinutes || body.WarningPercent is < 1 or > 99 || !Guid.TryParse(body.CalendarId, out _))
            throw new ArgumentException("Enter a valid SLA priority, target, warning level, and calendar.");
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            string policyId;
            await using (var policy = new MySqlCommand("SELECT id FROM sla_policies WHERE priority=@priority FOR UPDATE", connection, transaction))
            {
                policy.Parameters.AddWithValue("@priority", priority);
                policyId = Convert.ToString(await policy.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture) ?? throw new KeyNotFoundException();
            }
            await using (var activeCalendar = new MySqlCommand("SELECT id FROM sla_calendars WHERE id=@id AND active=1", connection, transaction))
            {
                activeCalendar.Parameters.AddWithValue("@id", body.CalendarId);
                if (await activeCalendar.ExecuteScalarAsync(ct) is null) throw new ArgumentException("Select an active SLA calendar.");
            }
            await using (var update = new MySqlCommand("UPDATE sla_policies SET response_minutes=@response,resolution_minutes=@resolution,warning_percent=@warning,active=@active,calendar_id=@calendar,updated_by_user_id=@actor,updated_at=UTC_TIMESTAMP(6) WHERE priority=@priority", connection, transaction))
            {
                update.Parameters.AddWithValue("@response", body.ResponseMinutes); update.Parameters.AddWithValue("@resolution", body.ResolutionMinutes); update.Parameters.AddWithValue("@warning", body.WarningPercent); update.Parameters.AddWithValue("@active", body.Active); update.Parameters.AddWithValue("@calendar", body.CalendarId); update.Parameters.AddWithValue("@actor", actorId); update.Parameters.AddWithValue("@priority", priority);
                await update.ExecuteNonQueryAsync(ct);
            }
            if (body.ApplyToOpenTickets && body.Active)
            {
                var calendar = await GetSlaCalendarById(connection, transaction, body.CalendarId, ct) ?? throw new ArgumentException("Select an active SLA calendar.");
                var open = new List<(string Id, DateTime Created, string Status, bool ResponseComplete, bool ResolutionComplete)>();
                await using (var ticketQuery = new MySqlCommand("SELECT t.id,t.created_at,t.status,s.response_completed_at,s.resolution_completed_at FROM tickets t JOIN ticket_sla s ON s.ticket_id=t.id WHERE t.priority=@priority AND t.status NOT IN ('RESOLVED','CLOSED','CANCELLED') FOR UPDATE", connection, transaction))
                {
                    ticketQuery.Parameters.AddWithValue("@priority", priority);
                    await using var reader = await ticketQuery.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct)) open.Add((reader.GetString(0), DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc), reader.GetString(2), !reader.IsDBNull(3), !reader.IsDBNull(4)));
                }
                var now = DateTime.UtcNow;
                foreach (var ticket in open)
                {
                    var rw = SlaBusinessCalendar.Add(calendar, ticket.Created, (int)Math.Ceiling(body.ResponseMinutes * body.WarningPercent / 100.0));
                    var rd = SlaBusinessCalendar.Add(calendar, ticket.Created, body.ResponseMinutes);
                    var sw = SlaBusinessCalendar.Add(calendar, ticket.Created, (int)Math.Ceiling(body.ResolutionMinutes * body.WarningPercent / 100.0));
                    var sd = SlaBusinessCalendar.Add(calendar, ticket.Created, body.ResolutionMinutes);
                    var paused = ticket.Status is "WAITING_FOR_CLIENT" or "WAITING_FOR_VENDOR" or "ON_HOLD";
                    await using var save = new MySqlCommand("UPDATE ticket_sla SET policy_id=@policy,calendar_id=@calendar,target_priority=@priority,response_warning_at=@rw,response_due_at=@rd,resolution_warning_at=@sw,resolution_due_at=@sd,paused_at=@paused,response_warning_remaining_minutes=@rwr,response_remaining_minutes=@rdr,resolution_warning_remaining_minutes=@swr,resolution_remaining_minutes=@sdr,updated_at=UTC_TIMESTAMP(6) WHERE ticket_id=@ticket", connection, transaction);
                    save.Parameters.AddWithValue("@policy", policyId); save.Parameters.AddWithValue("@calendar", body.CalendarId); save.Parameters.AddWithValue("@priority", priority); save.Parameters.AddWithValue("@rw", rw); save.Parameters.AddWithValue("@rd", rd); save.Parameters.AddWithValue("@sw", sw); save.Parameters.AddWithValue("@sd", sd); save.Parameters.AddWithValue("@paused", paused ? now : DBNull.Value);
                    save.Parameters.AddWithValue("@rwr", paused && !ticket.ResponseComplete ? SlaBusinessCalendar.Between(calendar, now, rw) : DBNull.Value);
                    save.Parameters.AddWithValue("@rdr", paused && !ticket.ResponseComplete ? SlaBusinessCalendar.Between(calendar, now, rd) : DBNull.Value);
                    save.Parameters.AddWithValue("@swr", paused && !ticket.ResolutionComplete ? SlaBusinessCalendar.Between(calendar, now, sw) : DBNull.Value);
                    save.Parameters.AddWithValue("@sdr", paused && !ticket.ResolutionComplete ? SlaBusinessCalendar.Between(calendar, now, sd) : DBNull.Value);
                    save.Parameters.AddWithValue("@ticket", ticket.Id);
                    await save.ExecuteNonQueryAsync(ct);
                }
            }
            await AddAudit(connection, transaction, actorId, "SLA_POLICY.UPDATED", "SLA_POLICY", priority, new { priority, body.ResponseMinutes, body.ResolutionMinutes, body.WarningPercent, body.Active, body.CalendarId, body.ApplyToOpenTickets }, ct);
            await transaction.CommitAsync(ct);
            return new { priority };
        }
        catch { await transaction.RollbackAsync(ct); throw; }
    }

    private async Task<bool> CanManageSla(string userId, CancellationToken ct) =>
        await IsAdministrator(userId, ct) || await HasPermission(userId, "SLA_MANAGE", new HashSet<string>(["MANAGER", "ADMIN"], StringComparer.Ordinal), ct);

    public async Task<List<Dictionary<string, object?>>> ListClients(string userId, CancellationToken ct)
    {
        if (ConnectionString is null) return [];
        await using var connection = CreateConnection(); await connection.OpenAsync(ct);
        const string sql = "SELECT c.id,c.name,c.ticket_prefix,c.status,c.primary_email,c.primary_phone,c.notes,c.created_at,c.updated_at FROM clients c WHERE EXISTS(SELECT 1 FROM user_roles ur JOIN users u ON u.id=ur.user_id WHERE ur.user_id=@user AND ur.role_code='ADMIN' AND u.active=1) OR EXISTS(SELECT 1 FROM client_users cu WHERE cu.user_id=@user AND cu.client_id=c.id) ORDER BY c.name";
        await using var command = new MySqlCommand(sql, connection); command.Parameters.AddWithValue("@user", userId);
        await using var reader = await command.ExecuteReaderAsync(ct); var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync(ct)) { var row = new Dictionary<string, object?>(StringComparer.Ordinal); for(var i=0;i<reader.FieldCount;i++) row[reader.GetName(i)]=ReadValue(reader,i); rows.Add(row); }
        return rows;
    }

    public async Task<List<Dictionary<string, object?>>> ListClientDirectory(string userId, string? clientId, string resource, CancellationToken ct)
    {
        if (ConnectionString is null) return [];
        var sql = resource switch
        {
            "contacts" => "SELECT x.id,x.client_id,x.site_id,x.user_id,x.name,x.email,x.phone,x.active,x.created_at,x.updated_at FROM contacts x",
            "sites" => "SELECT x.id,x.client_id,x.name,x.address,x.active,x.created_at,x.updated_at FROM sites x",
            "assets" => "SELECT x.id,x.client_id,x.site_id,x.name,x.asset_type,x.serial_number,x.external_reference,x.active,x.created_at,x.updated_at FROM assets x",
            _ => throw new ArgumentOutOfRangeException(nameof(resource))
        };
        await using var connection = CreateConnection(); await connection.OpenAsync(ct);
        sql += " WHERE (@client IS NULL OR x.client_id=@client) AND (EXISTS(SELECT 1 FROM user_roles ur JOIN users u ON u.id=ur.user_id WHERE ur.user_id=@user AND ur.role_code='ADMIN' AND u.active=1) OR EXISTS(SELECT 1 FROM client_users cu WHERE cu.user_id=@user AND cu.client_id=x.client_id)) ORDER BY x.name";
        await using var command = new MySqlCommand(sql, connection); command.Parameters.AddWithValue("@user", userId); command.Parameters.AddWithValue("@client", (object?)clientId ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(ct); var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync(ct)) { var row = new Dictionary<string, object?>(StringComparer.Ordinal); for(var i=0;i<reader.FieldCount;i++) row[reader.GetName(i)]=ReadValue(reader,i); rows.Add(row); }
        return rows;
    }

    public async Task<List<Dictionary<string, object?>>> ListCategories(CancellationToken ct)
    {
        if (ConnectionString is null) return [];
        await using var connection=CreateConnection(); await connection.OpenAsync(ct); await using var command=new MySqlCommand("SELECT id,parent_id,name,description,active,created_at,updated_at FROM categories ORDER BY parent_id,name",connection);
        await using var reader=await command.ExecuteReaderAsync(ct); var rows=new List<Dictionary<string,object?>>(); while(await reader.ReadAsync(ct)){var row=new Dictionary<string,object?>(StringComparer.Ordinal);for(var i=0;i<reader.FieldCount;i++)row[reader.GetName(i)]=ReadValue(reader,i);rows.Add(row);} return rows;
    }

    public async Task<List<Dictionary<string, object?>>> ListTags(CancellationToken ct)
    {
        if (ConnectionString is null) return [];
        await using var connection=CreateConnection(); await connection.OpenAsync(ct); await using var command=new MySqlCommand("SELECT id,name,description,active,created_at,updated_at FROM tags ORDER BY name",connection);
        await using var reader=await command.ExecuteReaderAsync(ct); var rows=new List<Dictionary<string,object?>>(); while(await reader.ReadAsync(ct)){var row=new Dictionary<string,object?>(StringComparer.Ordinal);for(var i=0;i<reader.FieldCount;i++)row[reader.GetName(i)]=ReadValue(reader,i);rows.Add(row);} return rows;
    }

    public async Task<List<Dictionary<string, object?>>> ListUsers(CancellationToken ct)
    {
        if (ConnectionString is null) return [];
        const string sql = "SELECT u.id,u.email,u.display_name,u.active,u.last_login_at,u.work_scope,u.change_access,u.supervisor_user_id,COALESCE((SELECT JSON_ARRAYAGG(ur.role_code) FROM user_roles ur WHERE ur.user_id=u.id),JSON_ARRAY()) AS roles,COALESCE((SELECT JSON_ARRAYAGG(cu.client_id) FROM client_users cu WHERE cu.user_id=u.id),JSON_ARRAY()) AS client_ids,COALESCE((SELECT JSON_ARRAYAGG(us.site_id) FROM user_sites us WHERE us.user_id=u.id),JSON_ARRAY()) AS site_ids,COALESCE((SELECT JSON_ARRAYAGG(up.permission_code) FROM user_permissions up WHERE up.user_id=u.id),JSON_ARRAY()) AS permissions,COALESCE((SELECT JSON_ARRAYAGG(du.department_id) FROM department_users du WHERE du.user_id=u.id),JSON_ARRAY()) AS department_ids,COALESCE((SELECT JSON_ARRAYAGG(du.department_id) FROM department_users du WHERE du.user_id=u.id AND du.is_lead=1),JSON_ARRAY()) AS lead_department_ids FROM users u ORDER BY u.display_name";
        await using var connection=CreateConnection(); await connection.OpenAsync(ct); await using var command=new MySqlCommand(sql,connection); await using var reader=await command.ExecuteReaderAsync(ct); var rows=new List<Dictionary<string,object?>>();
        while(await reader.ReadAsync(ct)){var row=new Dictionary<string,object?>(StringComparer.Ordinal);for(var i=0;i<reader.FieldCount;i++)row[reader.GetName(i)]=ReadValue(reader,i);rows.Add(row);} return rows;
    }

    public async Task<List<Dictionary<string, object?>>> ListAuditLogs(CancellationToken ct)
    {
        if (ConnectionString is null) return [];
        await using var connection=CreateConnection(); await connection.OpenAsync(ct);
        const string sql="SELECT id,actor_user_id,action,entity_type,entity_id,details,created_at FROM audit_logs ORDER BY created_at DESC,id DESC LIMIT 500";
        await using var command=new MySqlCommand(sql,connection); await using var reader=await command.ExecuteReaderAsync(ct); var rows=new List<Dictionary<string,object?>>();
        while(await reader.ReadAsync(ct)){var row=new Dictionary<string,object?>(StringComparer.Ordinal);for(var i=0;i<reader.FieldCount;i++)row[reader.GetName(i)]=ReadValue(reader,i);rows.Add(row);} return rows;
    }

    public async Task<Dictionary<string, object?>?> GetAdminOverview(string userId, CancellationToken ct)
    {
        if (ConnectionString is null || !await IsAdministrator(userId, ct)) return null;
        const string sql = "SELECT (SELECT COUNT(*) FROM users) AS users,(SELECT COUNT(*) FROM tickets) AS tickets,(SELECT COUNT(*) FROM clients) AS clients,(SELECT COUNT(*) FROM sites) AS sites,(SELECT COUNT(*) FROM assets) AS assets,(SELECT COUNT(*) FROM users WHERE active=1) AS active_users,(SELECT COUNT(*) FROM clients WHERE organization_type='CLIENT' AND status='ACTIVE') AS active_customers,(SELECT COUNT(*) FROM contacts WHERE active=1) AS active_contacts,(SELECT COUNT(*) FROM access_groups WHERE active=1) AS active_groups,(SELECT COUNT(*) FROM access_groups WHERE active=1 AND kind='CUSTOMER') AS customer_groups,(SELECT COUNT(*) FROM access_groups WHERE active=1 AND kind='STAFF') AS staff_groups";
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        await using var command = new MySqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = ReadValue(reader, i);
        return row;
    }

    public async Task<object?> GetAccessPolicies(string actorId, CancellationToken ct)
    {
        if (ConnectionString is null || !await IsAdministrator(actorId, ct)) return null;
        const string sql = "SELECT r.code,p.permissions,p.revision,(SELECT COUNT(*) FROM user_roles ur WHERE ur.role_code=r.code) AS users FROM roles r LEFT JOIN role_access_policies p ON p.role_code=r.code WHERE r.code IN ('USER','INTERN','TECHNICIAN','TEAM_LEAD','MANAGER','ADMIN') ORDER BY FIELD(r.code,'USER','INTERN','TECHNICIAN','TEAM_LEAD','MANAGER','ADMIN')";
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        await using var command = new MySqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var roles = new List<object>();
        while (await reader.ReadAsync(ct))
        {
            var code = reader.GetString(0);
            var defaults = DefaultRolePolicy(code);
            var available = AvailableRolePolicy(code);
            var current = code == "ADMIN" ? available : reader.IsDBNull(1) ? defaults : ParsePermissionArray(reader.GetString(1));
            roles.Add(new { code, permissions = current, revision = reader.IsDBNull(2) ? 0 : reader.GetInt32(2), defaults, editable = code is "TECHNICIAN" or "TEAM_LEAD" or "MANAGER", available, users = reader.GetInt32(3) });
        }
        return new { roles };
    }

    public async Task<object?> GetUserAccessPolicy(string actorId, string targetId, CancellationToken ct)
    {
        if (ConnectionString is null || !await IsAdministrator(actorId, ct)) return null;
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        await using var command = new MySqlCommand("SELECT u.id,u.display_name,u.email,COALESCE(p.allow_permissions,JSON_ARRAY()),COALESCE(p.deny_permissions,JSON_ARRAY()),COALESCE(p.revision,0) FROM users u LEFT JOIN user_access_policies p ON p.user_id=u.id WHERE u.id=@id", connection);
        command.Parameters.AddWithValue("@id", targetId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var id = reader.GetString(0);
        var name = reader.GetString(1);
        var email = reader.GetString(2);
        var allow = ParsePermissionArray(reader.GetString(3));
        var deny = ParsePermissionArray(reader.GetString(4));
        var revision = reader.GetInt32(5);
        await reader.DisposeAsync();
        var roles = new List<string>();
        await using (var roleQuery = new MySqlCommand("SELECT role_code FROM user_roles WHERE user_id=@id ORDER BY role_code", connection))
        {
            roleQuery.Parameters.AddWithValue("@id", targetId);
            await using var roleReader = await roleQuery.ExecuteReaderAsync(ct);
            while (await roleReader.ReadAsync(ct)) roles.Add(roleReader.GetString(0));
        }
        var groupPermissions = new HashSet<string>(StringComparer.Ordinal);
        await using (var groupQuery = new MySqlCommand("SELECT g.permissions FROM access_groups g JOIN staff_group_members gm ON gm.group_id=g.id WHERE gm.user_id=@id AND g.kind='STAFF' AND g.active=1", connection))
        {
            groupQuery.Parameters.AddWithValue("@id", targetId);
            await using var groupReader = await groupQuery.ExecuteReaderAsync(ct);
            while (await groupReader.ReadAsync(ct)) foreach (var permission in ParsePermissionArray(groupReader.GetString(0))) groupPermissions.Add(permission);
        }
        return new { id, name, email, roles, groupPermissions = groupPermissions.Order(StringComparer.Ordinal).ToArray(), allow, deny, revision };
    }

    private static string[] ParsePermissionArray(string json)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<string[]>(json) ?? []; }
        catch (System.Text.Json.JsonException) { return []; }
    }

    private static readonly string[] PolicyPermissionCodes = ["TICKETS_TEAM_VIEW","TICKETS_ASSIGN","TICKETS_APPROVE","CATEGORY_MANAGE","SLA_MANAGE","REPORTS_VIEW","USERS_MANAGE","CLIENTS_MANAGE","CONTACTS_MANAGE","SITES_MANAGE","TAGS_MANAGE","AUDIT_VIEW"];
    private static readonly string[] StaffPolicyCodes = ["TICKETS_TEAM_VIEW","TICKETS_ASSIGN","TICKETS_APPROVE","CATEGORY_MANAGE","SLA_MANAGE","REPORTS_VIEW","TAGS_MANAGE"];
    private static string[] DefaultRolePolicy(string code) => code switch
    {
        "TECHNICIAN" => ["TICKETS_TEAM_VIEW","TICKETS_ASSIGN"],
        "TEAM_LEAD" => ["TICKETS_TEAM_VIEW","TICKETS_ASSIGN","CATEGORY_MANAGE","TAGS_MANAGE","REPORTS_VIEW"],
        "MANAGER" => ["TICKETS_TEAM_VIEW","TICKETS_ASSIGN","TICKETS_APPROVE","CATEGORY_MANAGE","TAGS_MANAGE","SLA_MANAGE","REPORTS_VIEW","AUDIT_VIEW"],
        "ADMIN" => PolicyPermissionCodes,
        _ => []
    };
    private static string[] AvailableRolePolicy(string code) => code switch
    {
        "ADMIN" => PolicyPermissionCodes,
        "MANAGER" or "TEAM_LEAD" => [..StaffPolicyCodes,"AUDIT_VIEW"],
        "TECHNICIAN" => StaffPolicyCodes,
        _ => []
    };

    public async Task<object?> SaveAccessPolicy(string actorId, string kind, string target, int revision, IReadOnlyList<string>? allow, IReadOnlyList<string>? deny, CancellationToken ct)
    {
        if (ConnectionString is null || !await IsAdministrator(actorId, ct)) return null;
        if (revision is < 0 or > 2147483646 || allow is null || deny is null || allow.Count > 12 || deny.Count > 12 || kind is not ("role" or "user"))
            throw new ArgumentException("The access policy is invalid.");
        if (kind == "role" && target is not ("TECHNICIAN" or "TEAM_LEAD" or "MANAGER"))
            throw new ArgumentException("This access profile is fixed or unknown.");
        if (kind == "user" && !Guid.TryParse(target, out _)) throw new ArgumentException("The user ID is invalid.");
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            string[] roles;
            if (kind == "role")
            {
                await using var roleQuery = new MySqlCommand("SELECT code FROM roles WHERE code=@id FOR UPDATE", connection, transaction);
                roleQuery.Parameters.AddWithValue("@id", target);
                if (await roleQuery.ExecuteScalarAsync(ct) is null) throw new ArgumentException("Unknown role.");
                roles = [target];
            }
            else
            {
                await using var targetQuery = new MySqlCommand("SELECT id FROM users WHERE id=@id FOR UPDATE", connection, transaction);
                targetQuery.Parameters.AddWithValue("@id", target);
                if (await targetQuery.ExecuteScalarAsync(ct) is null) throw new KeyNotFoundException();
                roles = [];
                await using var roleQuery = new MySqlCommand("SELECT role_code FROM user_roles WHERE user_id=@id", connection, transaction);
                roleQuery.Parameters.AddWithValue("@id", target);
                await using var reader = await roleQuery.ExecuteReaderAsync(ct);
                var assigned = new List<string>();
                while (await reader.ReadAsync(ct)) assigned.Add(reader.GetString(0));
                roles = [..assigned];
            }
            if (roles.Length == 0 || roles.Any(role => role is "ADMIN" or "USER" or "INTERN" || AvailableRolePolicy(role).Length == 0))
                throw new ArgumentException("This access profile is fixed. Use People to manage its role and scope.");
            var available = roles.SelectMany(AvailableRolePolicy).ToHashSet(StringComparer.Ordinal);
            if (allow.Concat(deny).Any(permission => !available.Contains(permission)) || allow.Any(deny.Contains))
                throw new ArgumentException("Permissions must be supported by this access profile; allow and deny cannot overlap.");
            var table = kind == "role" ? "role_access_policies" : "user_access_policies";
            var key = kind == "role" ? "role_code" : "user_id";
            await using var previousQuery = new MySqlCommand($"SELECT revision FROM {table} WHERE {key}=@id FOR UPDATE", connection, transaction);
            previousQuery.Parameters.AddWithValue("@id", target);
            var previous = await previousQuery.ExecuteScalarAsync(ct);
            var currentRevision = previous is null ? 0 : Convert.ToInt32(previous, System.Globalization.CultureInfo.InvariantCulture);
            if (currentRevision != revision) throw new InvalidOperationException("Permissions changed since you opened them. Refresh before saving.");
            var next = revision + 1;
            var allowJson = System.Text.Json.JsonSerializer.Serialize(allow.Distinct(StringComparer.Ordinal).ToArray());
            var denyJson = System.Text.Json.JsonSerializer.Serialize(deny.Distinct(StringComparer.Ordinal).ToArray());
            if (kind == "role")
            {
                var sql = previous is null
                    ? "INSERT INTO role_access_policies(role_code,permissions,revision,updated_by) VALUES(@id,@allow,@revision,@actor)"
                    : "UPDATE role_access_policies SET permissions=@allow,revision=@revision,updated_by=@actor,updated_at=UTC_TIMESTAMP(6) WHERE role_code=@id";
                await using var save = new MySqlCommand(sql, connection, transaction);
                save.Parameters.AddWithValue("@id", target); save.Parameters.AddWithValue("@allow", allowJson); save.Parameters.AddWithValue("@revision", next); save.Parameters.AddWithValue("@actor", actorId);
                await save.ExecuteNonQueryAsync(ct);
            }
            else
            {
                var sql = previous is null
                    ? "INSERT INTO user_access_policies(user_id,allow_permissions,deny_permissions,revision,updated_by) VALUES(@id,@allow,@deny,@revision,@actor)"
                    : "UPDATE user_access_policies SET allow_permissions=@allow,deny_permissions=@deny,revision=@revision,updated_by=@actor,updated_at=UTC_TIMESTAMP(6) WHERE user_id=@id";
                await using var save = new MySqlCommand(sql, connection, transaction);
                save.Parameters.AddWithValue("@id", target); save.Parameters.AddWithValue("@allow", allowJson); save.Parameters.AddWithValue("@deny", denyJson); save.Parameters.AddWithValue("@revision", next); save.Parameters.AddWithValue("@actor", actorId);
                await save.ExecuteNonQueryAsync(ct);
            }
            await AddAudit(connection, transaction, actorId, kind == "role" ? "ROLE.PERMISSIONS_UPDATED" : "USER.PERMISSIONS_UPDATED", kind == "role" ? "ROLE" : "USER", target, new { target, beforeRevision = revision, after = new { allow, deny, revision = next } }, ct);
            await transaction.CommitAsync(ct);
            return new { saved = true, revision = next };
        }
        catch (MySqlException exception) when (exception.Number == 1062)
        {
            await transaction.RollbackAsync(ct);
            throw new InvalidOperationException("Permissions changed since you opened them. Refresh before saving.");
        }
        catch { await transaction.RollbackAsync(ct); throw; }
    }

    public async Task<object> ListNotifications(string userId, int limit, CancellationToken ct)
    {
        if (ConnectionString is null) return new { unread = 0, items = Array.Empty<object>() };
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        await using var grants = new MySqlCommand("SELECT EXISTS(SELECT 1 FROM user_roles WHERE user_id=@id AND role_code='ADMIN'),EXISTS(SELECT 1 FROM user_roles WHERE user_id=@id AND role_code IN ('TEAM_LEAD','MANAGER','ADMIN'))", connection);
        grants.Parameters.AddWithValue("@id", userId);
        await using var grantsReader = await grants.ExecuteReaderAsync(ct);
        await grantsReader.ReadAsync(ct);
        var admin = grantsReader.GetBoolean(0);
        var wide = grantsReader.GetBoolean(1);
        await grantsReader.DisposeAsync();
        var team = await HasDefaultTeamPermission(connection, userId, ct);
        const string access = "n.user_id=@id AND (n.ticket_id IS NULL OR @admin OR (EXISTS(SELECT 1 FROM client_users cu WHERE cu.user_id=@id AND cu.client_id=t.client_id) AND (@wide OR t.assigned_user_id=@id OR EXISTS(SELECT 1 FROM ticket_collaborators tc WHERE tc.ticket_id=t.id AND tc.user_id=@id) OR EXISTS(SELECT 1 FROM help_requests hr WHERE hr.ticket_id=t.id AND hr.status='PENDING' AND (hr.requested_user_id=@id OR EXISTS(SELECT 1 FROM department_users du WHERE du.department_id=hr.department_id AND du.user_id=@id))) OR (@team AND (NOT EXISTS(SELECT 1 FROM user_sites us WHERE us.user_id=@id) OR EXISTS(SELECT 1 FROM user_sites us WHERE us.user_id=@id AND us.site_id=t.site_id))))))";
        await using var unreadQuery = new MySqlCommand($"SELECT COUNT(*) FROM notifications n LEFT JOIN tickets t ON t.id=n.ticket_id WHERE {access} AND n.read_at IS NULL", connection);
        AddNotificationAccess(unreadQuery, userId, admin, wide, team);
        var unread = Convert.ToInt32(await unreadQuery.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
        await using var listQuery = new MySqlCommand($"SELECT n.id,n.ticket_id,n.change_id,n.type,n.title,n.body,n.read_at,n.created_at,t.ticket_number FROM notifications n LEFT JOIN tickets t ON t.id=n.ticket_id WHERE {access} ORDER BY n.created_at DESC LIMIT @limit", connection);
        AddNotificationAccess(listQuery, userId, admin, wide, team);
        listQuery.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 50));
        await using var reader = await listQuery.ExecuteReaderAsync(ct);
        var items = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = ReadValue(reader, i);
            items.Add(row);
        }
        return new { unread, items };
    }

    private static void AddNotificationAccess(MySqlCommand command, string userId, bool admin, bool wide, bool team)
    {
        command.Parameters.AddWithValue("@id", userId);
        command.Parameters.AddWithValue("@admin", admin);
        command.Parameters.AddWithValue("@wide", wide);
        command.Parameters.AddWithValue("@team", team);
    }

    public async Task<NotificationPreferencesRequest> GetNotificationPreferences(string userId, CancellationToken ct)
    {
        if (ConnectionString is null) return new NotificationPreferencesRequest();
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        await using var command = new MySqlCommand("SELECT ticket_assigned,ticket_transferred,public_reply,sla_at_risk,sla_breached,ticket_unattended FROM notification_preferences WHERE user_id=@id", connection);
        command.Parameters.AddWithValue("@id", userId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new NotificationPreferencesRequest { TicketAssigned = reader.GetBoolean(0), TicketTransferred = reader.GetBoolean(1), PublicReply = reader.GetBoolean(2), SlaAtRisk = reader.GetBoolean(3), SlaBreached = reader.GetBoolean(4), TicketUnattended = reader.GetBoolean(5) } : new NotificationPreferencesRequest();
    }

    public async Task SaveNotificationPreferences(string userId, NotificationPreferencesRequest body, CancellationToken ct)
    {
        if (ConnectionString is null) return;
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        const string sql = "INSERT INTO notification_preferences(user_id,ticket_assigned,ticket_transferred,public_reply,sla_at_risk,sla_breached,ticket_unattended) VALUES(@id,@assigned,@transferred,@reply,@risk,@breached,@unattended) ON DUPLICATE KEY UPDATE ticket_assigned=VALUES(ticket_assigned),ticket_transferred=VALUES(ticket_transferred),public_reply=VALUES(public_reply),sla_at_risk=VALUES(sla_at_risk),sla_breached=VALUES(sla_breached),ticket_unattended=VALUES(ticket_unattended),updated_at=UTC_TIMESTAMP(6)";
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@id", userId); command.Parameters.AddWithValue("@assigned", body.TicketAssigned); command.Parameters.AddWithValue("@transferred", body.TicketTransferred); command.Parameters.AddWithValue("@reply", body.PublicReply); command.Parameters.AddWithValue("@risk", body.SlaAtRisk); command.Parameters.AddWithValue("@breached", body.SlaBreached); command.Parameters.AddWithValue("@unattended", body.TicketUnattended);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkNotificationsRead(string userId, string? notificationId, CancellationToken ct)
    {
        if (ConnectionString is null) return;
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        var sql = notificationId is null ? "UPDATE notifications SET read_at=UTC_TIMESTAMP(6) WHERE user_id=@id AND read_at IS NULL" : "UPDATE notifications SET read_at=COALESCE(read_at,UTC_TIMESTAMP(6)) WHERE user_id=@id AND id=@notification";
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@id", userId);
        if (notificationId is not null) command.Parameters.AddWithValue("@notification", notificationId);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<Dictionary<string, object?>>> ListDepartments(CancellationToken ct)
    {
        if (ConnectionString is null) return [];
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        await using var command = new MySqlCommand("SELECT d.id,d.name,d.description,d.active,d.created_at,d.updated_at,(SELECT COUNT(*) FROM department_users du WHERE du.department_id=d.id) AS member_count FROM departments d WHERE d.active=1 ORDER BY d.name", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = ReadValue(reader, i);
            rows.Add(row);
        }
        return rows;
    }

    public async Task<List<Dictionary<string, object?>>?> ListStaffDirectory(string userId, CancellationToken ct)
    {
        if (ConnectionString is null) return null;
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        await using (var permission = new MySqlCommand("SELECT EXISTS(SELECT 1 FROM user_roles WHERE user_id=@id AND role_code IN ('INTERN','TECHNICIAN','TEAM_LEAD','MANAGER','ADMIN'))", connection))
        {
            permission.Parameters.AddWithValue("@id", userId);
            if (Convert.ToInt32(await permission.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture) != 1) return null;
        }
        const string sql = "SELECT u.id,u.display_name AS name,GROUP_CONCAT(DISTINCT CASE WHEN d.active=1 THEN d.name END ORDER BY d.name SEPARATOR ', ') AS department_name,GROUP_CONCAT(DISTINCT du.expertise ORDER BY du.expertise SEPARATOR '; ') AS expertise FROM users u LEFT JOIN department_users du ON du.user_id=u.id LEFT JOIN departments d ON d.id=du.department_id WHERE u.active=1 AND u.id<>@id AND EXISTS(SELECT 1 FROM user_roles ur WHERE ur.user_id=u.id AND ur.role_code IN ('INTERN','TECHNICIAN','TEAM_LEAD','MANAGER','ADMIN')) GROUP BY u.id,u.display_name ORDER BY u.display_name";
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@id", userId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = ReadValue(reader, i);
            rows.Add(row);
        }
        return rows;
    }

    public async Task<CreatedEntity?> CreateDepartment(string actorId, DepartmentRequest body, CancellationToken ct)
    {
        if (ConnectionString is null || !await IsAdministrator(actorId, ct)) return null;
        var name = body.Name?.Trim() ?? "";
        var description = body.Description?.Trim();
        if (name.Length is < 2 or > 150 || (description?.Length ?? 0) > 2000) throw new ArgumentException("Enter a department name between 2 and 150 characters.");
        var id = Guid.NewGuid().ToString("D");
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            await using (var insert = new MySqlCommand("INSERT INTO departments(id,name,description,active) VALUES(@id,@name,@description,1)", connection, transaction))
            {
                insert.Parameters.AddWithValue("@id", id); insert.Parameters.AddWithValue("@name", name); insert.Parameters.AddWithValue("@description", string.IsNullOrEmpty(description) ? DBNull.Value : description);
                await insert.ExecuteNonQueryAsync(ct);
            }
            await AddAudit(connection, transaction, actorId, "DEPARTMENT.CREATED", "DEPARTMENT", id, new { name }, ct);
            await transaction.CommitAsync(ct);
            return new CreatedEntity(id);
        }
        catch (MySqlException exception) when (exception.Number == 1062) { await transaction.RollbackAsync(ct); throw new ArgumentException("A department with this name already exists."); }
        catch { await transaction.RollbackAsync(ct); throw; }
    }

    public async Task<CreatedEntity?> SaveDepartmentMember(string actorId, string departmentId, DepartmentMemberRequest body, CancellationToken ct)
    {
        if (ConnectionString is null || !await IsAdministrator(actorId, ct)) return null;
        if (!Guid.TryParse(body.UserId, out _) || (body.Expertise?.Length ?? 0) > 1000) throw new ArgumentException("Select a valid user and keep expertise under 1,000 characters.");
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            await using (var department = new MySqlCommand("SELECT id FROM departments WHERE id=@id AND active=1 FOR UPDATE", connection, transaction))
            {
                department.Parameters.AddWithValue("@id", departmentId);
                if (await department.ExecuteScalarAsync(ct) is null) throw new KeyNotFoundException();
            }
            await using (var user = new MySqlCommand("SELECT id FROM users WHERE id=@id AND active=1", connection, transaction))
            {
                user.Parameters.AddWithValue("@id", body.UserId);
                if (await user.ExecuteScalarAsync(ct) is null) throw new ArgumentException("Select an active user.");
            }
            await using (var save = new MySqlCommand("INSERT INTO department_users(department_id,user_id,expertise,is_lead) VALUES(@department,@user,@expertise,@lead) ON DUPLICATE KEY UPDATE expertise=VALUES(expertise),is_lead=VALUES(is_lead)", connection, transaction))
            {
                save.Parameters.AddWithValue("@department", departmentId); save.Parameters.AddWithValue("@user", body.UserId); save.Parameters.AddWithValue("@expertise", (object?)body.Expertise?.Trim() ?? DBNull.Value); save.Parameters.AddWithValue("@lead", body.IsLead);
                await save.ExecuteNonQueryAsync(ct);
            }
            await AddAudit(connection, transaction, actorId, "DEPARTMENT.MEMBER_UPDATED", "DEPARTMENT", departmentId, new { body.UserId, body.IsLead }, ct);
            await transaction.CommitAsync(ct);
            return new CreatedEntity(departmentId);
        }
        catch { await transaction.RollbackAsync(ct); throw; }
    }

    public async Task<object> GetKnowledgeOptions(string userId, CancellationToken ct)
    {
        if (ConnectionString is null) return new { clients = Array.Empty<object>(), categories = Array.Empty<object>() };
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        await using var clients = new MySqlCommand("SELECT c.id,c.name FROM clients c WHERE c.status='ACTIVE' AND (EXISTS(SELECT 1 FROM user_roles ur WHERE ur.user_id=@id AND ur.role_code='ADMIN') OR EXISTS(SELECT 1 FROM client_users cu WHERE cu.user_id=@id AND cu.client_id=c.id)) ORDER BY c.name", connection);
        clients.Parameters.AddWithValue("@id", userId);
        var clientRows = await ExecuteRows(clients, ct);
        await using var categories = new MySqlCommand("SELECT id,name FROM categories WHERE active=1 ORDER BY name", connection);
        var categoryRows = await ExecuteRows(categories, ct);
        return new { clients = clientRows, categories = categoryRows };
    }

    public async Task<object> GetServiceCatalogue(string userId, CancellationToken ct)
    {
        if (ConnectionString is null) return new { items = Array.Empty<object>(), canManage = false, isAdmin = false };
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        var (admin, manager, _, _) = await GetKnowledgeRoles(connection, userId, ct);
        const string sql = "SELECT s.id,s.name,s.description,s.instructions,s.category_id,s.client_id,s.active,s.created_by_user_id,s.updated_at,c.name AS category_name FROM service_templates s JOIN categories c ON c.id=s.category_id WHERE (@admin OR s.client_id IS NULL OR EXISTS(SELECT 1 FROM client_users cu WHERE cu.user_id=@id AND cu.client_id=s.client_id)) AND (@manager OR s.active=1) ORDER BY s.name LIMIT 200";
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@id", userId); command.Parameters.AddWithValue("@admin", admin); command.Parameters.AddWithValue("@manager", manager);
        var items = await ExecuteRows(command, ct);
        return new { items, canManage = manager, isAdmin = admin };
    }

    public async Task<object?> ListKnowledge(string userId, string search, int page, string? articleId, CancellationToken ct)
    {
        if (ConnectionString is null) return null;
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        var (admin, manager, staff, customer) = await GetKnowledgeRoles(connection, userId, ct);
        if (!staff && !customer) return null;
        const string source = " FROM knowledge_articles a JOIN users u ON u.id=a.author_user_id LEFT JOIN categories c ON c.id=a.category_id";
        const string where = " WHERE (@admin OR a.client_id IS NULL OR EXISTS(SELECT 1 FROM client_users cu WHERE cu.user_id=@id AND cu.client_id=a.client_id)) AND (@manager OR (a.status='PUBLISHED' AND (@staff OR a.visibility='CLIENT'))) AND (@search='' OR a.title LIKE @pattern OR a.body LIKE @pattern) AND (@article IS NULL OR a.id=@article)";
        await using var count = new MySqlCommand("SELECT COUNT(*)" + source + where, connection);
        AddKnowledgeParameters(count, userId, admin, manager, staff, search, articleId);
        var total = Convert.ToInt32(await count.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
        await using var query = new MySqlCommand("SELECT a.id,a.title,a.body,a.category_id,a.client_id,a.visibility,a.status,a.revision,a.updated_at,u.display_name AS author_name,c.name AS category_name" + source + where + " ORDER BY a.updated_at DESC LIMIT @take OFFSET @skip", connection);
        AddKnowledgeParameters(query, userId, admin, manager, staff, search, articleId);
        query.Parameters.AddWithValue("@take", articleId is null ? 20 : 1); query.Parameters.AddWithValue("@skip", articleId is null ? (page - 1) * 20 : 0);
        var items = await ExecuteRows(query, ct);
        return articleId is null ? new { total, items, canManage = manager, isAdmin = admin } : items.FirstOrDefault();
    }

    private static async Task<(bool Admin, bool Manager, bool Staff, bool Customer)> GetKnowledgeRoles(MySqlConnection connection, string userId, CancellationToken ct)
    {
        await using var command = new MySqlCommand("SELECT EXISTS(SELECT 1 FROM user_roles WHERE user_id=@id AND role_code='ADMIN'),EXISTS(SELECT 1 FROM user_roles WHERE user_id=@id AND role_code IN ('MANAGER','ADMIN')),EXISTS(SELECT 1 FROM user_roles WHERE user_id=@id AND role_code IN ('INTERN','TECHNICIAN','TEAM_LEAD','MANAGER','ADMIN')),EXISTS(SELECT 1 FROM user_roles WHERE user_id=@id AND role_code='USER')", connection);
        command.Parameters.AddWithValue("@id", userId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (reader.GetBoolean(0), reader.GetBoolean(1), reader.GetBoolean(2), reader.GetBoolean(3));
    }

    private static void AddKnowledgeParameters(MySqlCommand command, string userId, bool admin, bool manager, bool staff, string search, string? articleId)
    {
        command.Parameters.AddWithValue("@id", userId); command.Parameters.AddWithValue("@admin", admin); command.Parameters.AddWithValue("@manager", manager); command.Parameters.AddWithValue("@staff", staff); command.Parameters.AddWithValue("@search", search); command.Parameters.AddWithValue("@pattern", $"%{search.Replace("%", "\\%").Replace("_", "\\_")}%"); command.Parameters.AddWithValue("@article", (object?)articleId ?? DBNull.Value);
    }

    private static async Task<List<Dictionary<string, object?>>> ExecuteRows(MySqlCommand command, CancellationToken ct)
    {
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = ReadValue(reader, i);
            rows.Add(row);
        }
        return rows;
    }

    public async Task<CreatedEntity?> SaveServiceTemplate(string actorId, string? id, ServiceTemplateRequest body, CancellationToken ct)
    {
        if (ConnectionString is null) return null;
        var name = body.Name?.Trim() ?? "";
        var description = body.Description?.Trim() ?? "";
        var instructions = body.Instructions?.Trim() ?? "";
        if (name.Length is < 3 or > 200 || description.Length is < 3 or > 5000 || instructions.Length is < 3 or > 20000 || !Guid.TryParse(body.CategoryId, out _) || (body.ClientId is not null && !Guid.TryParse(body.ClientId, out _)))
            throw new ArgumentException("Enter a valid template name, description, instructions, category, and client.");
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        if (!await CanManageKnowledgeScope(connection, actorId, body.ClientId, ct)) return null;
        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            if (id is not null)
            {
                await using var existing = new MySqlCommand("SELECT client_id FROM service_templates WHERE id=@id FOR UPDATE", connection, transaction);
                existing.Parameters.AddWithValue("@id", id);
                var oldClient = await existing.ExecuteScalarAsync(ct);
                if (oldClient is null) throw new KeyNotFoundException();
                if (!await CanManageKnowledgeScope(connection, actorId, oldClient is DBNull ? null : Convert.ToString(oldClient, System.Globalization.CultureInfo.InvariantCulture), ct, transaction)) { await transaction.RollbackAsync(ct); return null; }
            }
            await ValidateKnowledgeReferences(connection, transaction, body.CategoryId, body.ClientId, ct);
            var savedId = id ?? Guid.NewGuid().ToString("D");
            var sql = id is null
                ? "INSERT INTO service_templates(id,name,description,instructions,category_id,client_id,active,created_by_user_id) VALUES(@id,@name,@description,@instructions,@category,@client,@active,@actor)"
                : "UPDATE service_templates SET name=@name,description=@description,instructions=@instructions,category_id=@category,client_id=@client,active=@active,updated_at=UTC_TIMESTAMP(6) WHERE id=@id";
            await using (var save = new MySqlCommand(sql, connection, transaction))
            {
                save.Parameters.AddWithValue("@id", savedId); save.Parameters.AddWithValue("@name", name); save.Parameters.AddWithValue("@description", description); save.Parameters.AddWithValue("@instructions", instructions); save.Parameters.AddWithValue("@category", body.CategoryId); save.Parameters.AddWithValue("@client", (object?)body.ClientId ?? DBNull.Value); save.Parameters.AddWithValue("@active", body.Active); save.Parameters.AddWithValue("@actor", actorId);
                await save.ExecuteNonQueryAsync(ct);
            }
            await AddAudit(connection, transaction, actorId, "SERVICE.TEMPLATE_SAVED", "KNOWLEDGE", savedId, new { name, body.Active }, ct);
            await transaction.CommitAsync(ct);
            return new CreatedEntity(savedId);
        }
        catch { await transaction.RollbackAsync(ct); throw; }
    }

    public async Task<CreatedEntity?> SaveKnowledgeArticle(string actorId, string? id, KnowledgeArticleRequest body, CancellationToken ct)
    {
        if (ConnectionString is null) return null;
        var title = body.Title?.Trim() ?? "";
        var articleBody = body.Body?.Trim() ?? "";
        if (title.Length is < 3 or > 200 || articleBody.Length is < 10 or > 50000 || body.Visibility is not ("STAFF" or "CLIENT") || body.Status is not ("DRAFT" or "PUBLISHED" or "ARCHIVED") || (body.CategoryId is not null && !Guid.TryParse(body.CategoryId, out _)) || (body.ClientId is not null && !Guid.TryParse(body.ClientId, out _)))
            throw new ArgumentException("Enter a valid article title, body, visibility, status, category, and client.");
        if (id is not null && body.Revision is null or < 1) throw new ArgumentException("Reload the article before editing.");
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        if (!await CanManageKnowledgeScope(connection, actorId, body.ClientId, ct)) return null;
        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            if (id is not null)
            {
                await using var existing = new MySqlCommand("SELECT client_id,revision FROM knowledge_articles WHERE id=@id FOR UPDATE", connection, transaction);
                existing.Parameters.AddWithValue("@id", id);
                await using var reader = await existing.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct)) throw new KeyNotFoundException();
                var oldClient = reader.IsDBNull(0) ? null : reader.GetString(0);
                var revision = reader.GetInt32(1);
                await reader.DisposeAsync();
                if (!await CanManageKnowledgeScope(connection, actorId, oldClient, ct, transaction)) { await transaction.RollbackAsync(ct); return null; }
                if (revision != body.Revision) throw new InvalidOperationException("Article changed since you opened it. Reload before saving.");
            }
            await ValidateKnowledgeReferences(connection, transaction, body.CategoryId, body.ClientId, ct);
            var savedId = id ?? Guid.NewGuid().ToString("D");
            var sql = id is null
                ? "INSERT INTO knowledge_articles(id,title,body,category_id,client_id,visibility,status,author_user_id) VALUES(@id,@title,@body,@category,@client,@visibility,@status,@actor)"
                : "UPDATE knowledge_articles SET title=@title,body=@body,category_id=@category,client_id=@client,visibility=@visibility,status=@status,revision=revision+1,updated_at=UTC_TIMESTAMP(6) WHERE id=@id AND revision=@revision";
            await using (var save = new MySqlCommand(sql, connection, transaction))
            {
                save.Parameters.AddWithValue("@id", savedId); save.Parameters.AddWithValue("@title", title); save.Parameters.AddWithValue("@body", articleBody); save.Parameters.AddWithValue("@category", (object?)body.CategoryId ?? DBNull.Value); save.Parameters.AddWithValue("@client", (object?)body.ClientId ?? DBNull.Value); save.Parameters.AddWithValue("@visibility", body.Visibility); save.Parameters.AddWithValue("@status", body.Status); save.Parameters.AddWithValue("@actor", actorId); save.Parameters.AddWithValue("@revision", (object?)body.Revision ?? DBNull.Value);
                if (await save.ExecuteNonQueryAsync(ct) == 0) throw new InvalidOperationException("Article changed since you opened it. Reload before saving.");
            }
            await AddAudit(connection, transaction, actorId, "KNOWLEDGE.ARTICLE_SAVED", "KNOWLEDGE", savedId, new { title, body.Status, body.Visibility, body.ClientId }, ct);
            await transaction.CommitAsync(ct);
            return new CreatedEntity(savedId);
        }
        catch { await transaction.RollbackAsync(ct); throw; }
    }

    public async Task<object?> LinkKnowledgeTicket(string actorId, string articleId, string ticketId, CancellationToken ct)
    {
        if (ConnectionString is null) return null;
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        var (_, _, staff, _) = await GetKnowledgeRoles(connection, actorId, ct);
        if (!staff) return null;
        var article = await ListKnowledge(actorId, "", 1, articleId, ct);
        var ticket = await GetTicketDetail(actorId, ticketId, ct);
        if (article is null || ticket is null) return null;
        var articleClient = System.Text.Json.JsonSerializer.SerializeToElement(article).GetProperty("client_id");
        var ticketClient = System.Text.Json.JsonSerializer.SerializeToElement(ticket).GetProperty("ticket").GetProperty("client_id").GetString();
        if (articleClient.ValueKind != System.Text.Json.JsonValueKind.Null && articleClient.GetString() != ticketClient)
            throw new ArgumentException("The article belongs to another client.");
        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            await using (var insert = new MySqlCommand("INSERT IGNORE INTO ticket_knowledge_links(ticket_id,article_id,linked_by_user_id) VALUES(@ticket,@article,@actor)", connection, transaction))
            {
                insert.Parameters.AddWithValue("@ticket", ticketId); insert.Parameters.AddWithValue("@article", articleId); insert.Parameters.AddWithValue("@actor", actorId);
                await insert.ExecuteNonQueryAsync(ct);
            }
            await AddAudit(connection, transaction, actorId, "KNOWLEDGE.LINKED_TO_TICKET", "KNOWLEDGE", articleId, new { ticketId }, ct);
            await transaction.CommitAsync(ct);
            return new { linked = true };
        }
        catch { await transaction.RollbackAsync(ct); throw; }
    }

    private static async Task<bool> CanManageKnowledgeScope(MySqlConnection connection, string userId, string? clientId, CancellationToken ct, MySqlTransaction? transaction = null)
    {
        await using var command = new MySqlCommand("SELECT EXISTS(SELECT 1 FROM user_roles WHERE user_id=@id AND role_code='ADMIN'),EXISTS(SELECT 1 FROM user_roles WHERE user_id=@id AND role_code='MANAGER'),EXISTS(SELECT 1 FROM client_users WHERE user_id=@id AND client_id=@client)", connection, transaction);
        command.Parameters.AddWithValue("@id", userId); command.Parameters.AddWithValue("@client", (object?)clientId ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return reader.GetBoolean(0) || (clientId is not null && reader.GetBoolean(1) && reader.GetBoolean(2));
    }

    private static async Task ValidateKnowledgeReferences(MySqlConnection connection, MySqlTransaction transaction, string? categoryId, string? clientId, CancellationToken ct)
    {
        if (categoryId is not null)
        {
            await using var category = new MySqlCommand("SELECT id FROM categories WHERE id=@id AND active=1", connection, transaction);
            category.Parameters.AddWithValue("@id", categoryId);
            if (await category.ExecuteScalarAsync(ct) is null) throw new ArgumentException("Select an active category.");
        }
        if (clientId is not null)
        {
            await using var client = new MySqlCommand("SELECT id FROM clients WHERE id=@id AND status='ACTIVE'", connection, transaction);
            client.Parameters.AddWithValue("@id", clientId);
            if (await client.ExecuteScalarAsync(ct) is null) throw new ArgumentException("Select an active client.");
        }
    }

    public async Task<object?> GetProblemOptions(string userId, CancellationToken ct)
    {
        if (ConnectionString is null) return null;
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        var (admin, staff, _) = await GetProblemRoles(connection, userId, ct);
        if (!staff) return null;
        await using var clients = new MySqlCommand("SELECT c.id,c.name FROM clients c WHERE c.status='ACTIVE' AND (@admin OR EXISTS(SELECT 1 FROM client_users cu WHERE cu.user_id=@id AND cu.client_id=c.id)) ORDER BY c.name", connection);
        clients.Parameters.AddWithValue("@admin", admin); clients.Parameters.AddWithValue("@id", userId);
        var clientRows = await ExecuteRows(clients, ct);
        await using var sites = new MySqlCommand("SELECT s.id,s.client_id,s.name FROM sites s WHERE s.active=1 AND (@admin OR (EXISTS(SELECT 1 FROM client_users cu WHERE cu.user_id=@id AND cu.client_id=s.client_id) AND (NOT EXISTS(SELECT 1 FROM user_sites us WHERE us.user_id=@id) OR EXISTS(SELECT 1 FROM user_sites us WHERE us.user_id=@id AND us.site_id=s.id)))) ORDER BY s.name", connection);
        sites.Parameters.AddWithValue("@admin", admin); sites.Parameters.AddWithValue("@id", userId);
        var siteRows = await ExecuteRows(sites, ct);
        return new { clients = clientRows, sites = siteRows };
    }

    public async Task<object?> ListProblems(string userId, string search, string? status, string? clientId, int page, int pageSize, CancellationToken ct)
    {
        if (ConnectionString is null) return null;
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        var (admin, staff, _) = await GetProblemRoles(connection, userId, ct);
        if (!staff) return null;
        const string from = " FROM problems p JOIN clients cl ON cl.id=p.client_id JOIN users u ON u.id=p.owner_user_id";
        const string where = " WHERE (@admin OR (EXISTS(SELECT 1 FROM client_users cu WHERE cu.user_id=@id AND cu.client_id=p.client_id) AND (NOT EXISTS(SELECT 1 FROM user_sites us WHERE us.user_id=@id) OR EXISTS(SELECT 1 FROM user_sites us WHERE us.user_id=@id AND us.site_id=p.site_id)))) AND (@client IS NULL OR p.client_id=@client) AND (@status IS NULL OR p.status=@status) AND (@search='' OR p.problem_number LIKE @pattern OR p.title LIKE @pattern)";
        await using var count = new MySqlCommand("SELECT COUNT(*)" + from + where, connection);
        AddProblemParameters(count, userId, admin, search, status, clientId);
        var total = Convert.ToInt32(await count.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
        await using var query = new MySqlCommand("SELECT p.id,p.number,p.problem_number,p.client_id,p.site_id,p.title,p.description,p.priority,p.status,p.root_cause,p.workaround,p.resolution,p.owner_user_id,p.created_by_user_id,p.revision,p.created_at,p.updated_at,cl.name AS client_name,u.display_name AS owner_name" + from + where + " ORDER BY p.updated_at DESC,p.id LIMIT @take OFFSET @skip", connection);
        AddProblemParameters(query, userId, admin, search, status, clientId);
        query.Parameters.AddWithValue("@take", pageSize); query.Parameters.AddWithValue("@skip", (page - 1) * pageSize);
        var items = await ExecuteRows(query, ct);
        return new { total, items };
    }

    public async Task<CreatedEntity?> CreateProblem(string actorId, CreateProblemRequest body, CancellationToken ct)
    {
        if (ConnectionString is null) return null;
        var title = body.Title?.Trim() ?? "";
        var description = body.Description?.Trim() ?? "";
        if (!Guid.TryParse(body.ClientId, out _) || (body.SiteId is not null && !Guid.TryParse(body.SiteId, out _)) || title.Length is < 3 or > 200 || description.Length is < 3 or > 10000 || body.Priority is not ("P1_CRITICAL" or "P2_HIGH" or "P3_MEDIUM" or "P4_LOW"))
            throw new ArgumentException("Enter a valid client, site, title, description, and priority.");
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        var (admin, staff, _) = await GetProblemRoles(connection, actorId, ct);
        if (!staff) return null;
        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            await using (var scope = new MySqlCommand("SELECT EXISTS(SELECT 1 FROM clients WHERE id=@client AND status='ACTIVE'),EXISTS(SELECT 1 FROM client_users WHERE user_id=@actor AND client_id=@client),EXISTS(SELECT 1 FROM user_sites WHERE user_id=@actor),IF(@site IS NULL,1,EXISTS(SELECT 1 FROM sites WHERE id=@site AND client_id=@client AND active=1)),IF(@site IS NULL,0,EXISTS(SELECT 1 FROM user_sites WHERE user_id=@actor AND site_id=@site))", connection, transaction))
            {
                scope.Parameters.AddWithValue("@client", body.ClientId); scope.Parameters.AddWithValue("@actor", actorId); scope.Parameters.AddWithValue("@site", (object?)body.SiteId ?? DBNull.Value);
                await using var reader = await scope.ExecuteReaderAsync(ct);
                await reader.ReadAsync(ct);
                var activeClient = reader.GetBoolean(0); var hasClient = reader.GetBoolean(1); var hasSites = reader.GetBoolean(2); var validSite = reader.GetBoolean(3); var hasSite = reader.GetBoolean(4);
                await reader.DisposeAsync();
                if (!activeClient || (!admin && !hasClient) || !validSite || (!admin && hasSites && !hasSite && body.SiteId is not null)) { await transaction.RollbackAsync(ct); return null; }
                if (!admin && hasSites && body.SiteId is null) throw new ArgumentException("Select a site within your access scope.");
            }
            var id = Guid.NewGuid().ToString("D");
            await using (var insert = new MySqlCommand("INSERT INTO problems(id,client_id,site_id,title,description,priority,owner_user_id,created_by_user_id) VALUES(@id,@client,@site,@title,@description,@priority,@actor,@actor)", connection, transaction))
            {
                insert.Parameters.AddWithValue("@id", id); insert.Parameters.AddWithValue("@client", body.ClientId); insert.Parameters.AddWithValue("@site", (object?)body.SiteId ?? DBNull.Value); insert.Parameters.AddWithValue("@title", title); insert.Parameters.AddWithValue("@description", description); insert.Parameters.AddWithValue("@priority", body.Priority); insert.Parameters.AddWithValue("@actor", actorId);
                await insert.ExecuteNonQueryAsync(ct);
            }
            await using (var history = new MySqlCommand("INSERT INTO problem_history(id,problem_id,actor_user_id,action,details) VALUES(@id,@problem,@actor,'PROBLEM.CREATED',JSON_OBJECT())", connection, transaction))
            {
                history.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("D")); history.Parameters.AddWithValue("@problem", id); history.Parameters.AddWithValue("@actor", actorId);
                await history.ExecuteNonQueryAsync(ct);
            }
            await AddAudit(connection, transaction, actorId, "PROBLEM.CREATED", "PROBLEM", id, new { }, ct);
            await transaction.CommitAsync(ct);
            return new CreatedEntity(id);
        }
        catch { await transaction.RollbackAsync(ct); throw; }
    }

    public async Task<object?> GetProblemDetail(string userId, string problemId, CancellationToken ct)
    {
        if (ConnectionString is null) return null;
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        var (admin, staff, manager) = await GetProblemRoles(connection, userId, ct);
        if (!staff) return null;
        const string scope = "(@admin OR (EXISTS(SELECT 1 FROM client_users cu WHERE cu.user_id=@actor AND cu.client_id=p.client_id) AND (NOT EXISTS(SELECT 1 FROM user_sites us WHERE us.user_id=@actor) OR EXISTS(SELECT 1 FROM user_sites us WHERE us.user_id=@actor AND us.site_id=p.site_id))))";
        await using var problemQuery = new MySqlCommand("SELECT p.id,p.number,p.problem_number,p.client_id,p.site_id,p.title,p.description,p.priority,p.status,p.root_cause,p.workaround,p.resolution,p.owner_user_id,p.created_by_user_id,p.revision,p.created_at,p.updated_at,cl.name AS client_name,u.display_name AS owner_name FROM problems p JOIN clients cl ON cl.id=p.client_id JOIN users u ON u.id=p.owner_user_id WHERE p.id=@problem AND " + scope, connection);
        problemQuery.Parameters.AddWithValue("@problem", problemId); problemQuery.Parameters.AddWithValue("@actor", userId); problemQuery.Parameters.AddWithValue("@admin", admin);
        var problem = (await ExecuteRows(problemQuery, ct)).FirstOrDefault();
        if (problem is null) return null;
        var clientId = Convert.ToString(problem["client_id"], System.Globalization.CultureInfo.InvariantCulture)!;
        var siteId = Convert.ToString(problem["site_id"], System.Globalization.CultureInfo.InvariantCulture);
        var canEdit = manager || Convert.ToString(problem["owner_user_id"], System.Globalization.CultureInfo.InvariantCulture) == userId;
        var team = await HasDefaultTeamPermission(connection, userId, ct);
        const string linkedSql = "SELECT t.id,t.ticket_number,t.subject,t.status,t.priority FROM problem_tickets pt JOIN tickets t ON t.id=pt.ticket_id WHERE pt.problem_id=@problem AND (@admin OR (EXISTS(SELECT 1 FROM client_users cu WHERE cu.user_id=@actor AND cu.client_id=t.client_id) AND (@manager OR t.assigned_user_id=@actor OR EXISTS(SELECT 1 FROM ticket_collaborators tc WHERE tc.ticket_id=t.id AND tc.user_id=@actor) OR EXISTS(SELECT 1 FROM help_requests hr WHERE hr.ticket_id=t.id AND hr.status='PENDING' AND (hr.requested_user_id=@actor OR EXISTS(SELECT 1 FROM department_users du WHERE du.department_id=hr.department_id AND du.user_id=@actor))) OR (@team AND (NOT EXISTS(SELECT 1 FROM user_sites us WHERE us.user_id=@actor) OR EXISTS(SELECT 1 FROM user_sites us WHERE us.user_id=@actor AND us.site_id=t.site_id)))))) ORDER BY pt.created_at";
        await using var linkedQuery = new MySqlCommand(linkedSql, connection);
        linkedQuery.Parameters.AddWithValue("@problem", problemId); linkedQuery.Parameters.AddWithValue("@actor", userId); linkedQuery.Parameters.AddWithValue("@admin", admin); linkedQuery.Parameters.AddWithValue("@manager", manager); linkedQuery.Parameters.AddWithValue("@team", team);
        var tickets = await ExecuteRows(linkedQuery, ct);
        await using var historyQuery = new MySqlCommand("SELECT h.id,h.problem_id,h.actor_user_id,h.action,h.details,h.created_at,u.display_name AS actor_name FROM problem_history h JOIN users u ON u.id=h.actor_user_id WHERE h.problem_id=@problem ORDER BY h.created_at DESC,h.id DESC LIMIT 200", connection);
        historyQuery.Parameters.AddWithValue("@problem", problemId);
        var history = await ExecuteRows(historyQuery, ct);
        var owners = new List<Dictionary<string, object?>>();
        if (manager)
        {
            const string ownersSql = "SELECT DISTINCT u.id,u.display_name FROM users u JOIN user_roles ur ON ur.user_id=u.id WHERE u.active=1 AND ur.role_code IN ('TECHNICIAN','TEAM_LEAD','MANAGER','ADMIN') AND (EXISTS(SELECT 1 FROM user_roles ar WHERE ar.user_id=u.id AND ar.role_code='ADMIN') OR EXISTS(SELECT 1 FROM client_users cu WHERE cu.user_id=u.id AND cu.client_id=@client)) AND (NOT EXISTS(SELECT 1 FROM user_sites us WHERE us.user_id=u.id) OR EXISTS(SELECT 1 FROM user_sites us WHERE us.user_id=u.id AND us.site_id=@site)) ORDER BY u.display_name";
            await using var ownersQuery = new MySqlCommand(ownersSql, connection);
            ownersQuery.Parameters.AddWithValue("@client", clientId); ownersQuery.Parameters.AddWithValue("@site", (object?)siteId ?? DBNull.Value);
            owners = await ExecuteRows(ownersQuery, ct);
        }
        return new { problem, tickets, history, owners, canEdit, canManage = manager };
    }

    public async Task<object?> ChangeProblemTicketLink(string actorId, string problemId, string ticketId, bool remove, CancellationToken ct)
    {
        if (ConnectionString is null) return null;
        var problemDetail = await GetProblemDetail(actorId, problemId, ct);
        var ticketDetail = await GetTicketDetail(actorId, ticketId, ct);
        if (problemDetail is null || ticketDetail is null) return null;
        var problem = System.Text.Json.JsonSerializer.SerializeToElement(problemDetail).GetProperty("problem");
        var ticket = System.Text.Json.JsonSerializer.SerializeToElement(ticketDetail).GetProperty("ticket");
        var clientId = problem.GetProperty("client_id").GetString();
        if (!System.Text.Json.JsonSerializer.SerializeToElement(problemDetail).GetProperty("canEdit").GetBoolean()) return null;
        if (problem.GetProperty("status").GetString() == "CLOSED") throw new ArgumentException("Reopen the problem before changing links.");
        if (ticket.GetProperty("client_id").GetString() != clientId) throw new ArgumentException("The ticket must belong to the same client.");
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            await using (var current = new MySqlCommand("SELECT status FROM problems WHERE id=@id FOR UPDATE", connection, transaction))
            {
                current.Parameters.AddWithValue("@id", problemId);
                var status = Convert.ToString(await current.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
                if (status is null) throw new KeyNotFoundException();
                if (status == "CLOSED") throw new ArgumentException("Reopen the problem before changing links.");
            }
            if (!remove)
            {
                await using var count = new MySqlCommand("SELECT COUNT(*) FROM problem_tickets WHERE problem_id=@id", connection, transaction);
                count.Parameters.AddWithValue("@id", problemId);
                if (Convert.ToInt32(await count.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture) >= 100) throw new ArgumentException("A problem can link up to 100 tickets.");
            }
            var sql = remove ? "DELETE FROM problem_tickets WHERE problem_id=@problem AND ticket_id=@ticket" : "INSERT IGNORE INTO problem_tickets(problem_id,ticket_id,client_id,linked_by_user_id) VALUES(@problem,@ticket,@client,@actor)";
            int changed;
            await using (var command = new MySqlCommand(sql, connection, transaction))
            {
                command.Parameters.AddWithValue("@problem", problemId); command.Parameters.AddWithValue("@ticket", ticketId); command.Parameters.AddWithValue("@client", clientId); command.Parameters.AddWithValue("@actor", actorId);
                changed = await command.ExecuteNonQueryAsync(ct);
            }
            if (changed > 0)
            {
                var action = remove ? "PROBLEM.TICKET_UNLINKED" : "PROBLEM.TICKET_LINKED";
                await using (var history = new MySqlCommand("INSERT INTO problem_history(id,problem_id,actor_user_id,action,details) VALUES(@id,@problem,@actor,@action,CAST(@details AS JSON))", connection, transaction))
                {
                    history.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("D")); history.Parameters.AddWithValue("@problem", problemId); history.Parameters.AddWithValue("@actor", actorId); history.Parameters.AddWithValue("@action", action); history.Parameters.AddWithValue("@details", System.Text.Json.JsonSerializer.Serialize(new { ticketId }));
                    await history.ExecuteNonQueryAsync(ct);
                }
                await AddAudit(connection, transaction, actorId, action, "PROBLEM", problemId, new { ticketId }, ct);
            }
            await transaction.CommitAsync(ct);
            return new { ok = true };
        }
        catch { await transaction.RollbackAsync(ct); throw; }
    }

    private static async Task<(bool Admin, bool Staff, bool Manager)> GetProblemRoles(MySqlConnection connection, string userId, CancellationToken ct)
    {
        await using var command = new MySqlCommand("SELECT EXISTS(SELECT 1 FROM user_roles WHERE user_id=@id AND role_code='ADMIN'),EXISTS(SELECT 1 FROM user_roles WHERE user_id=@id AND role_code IN ('TECHNICIAN','TEAM_LEAD','MANAGER','ADMIN')),EXISTS(SELECT 1 FROM user_roles WHERE user_id=@id AND role_code IN ('TEAM_LEAD','MANAGER','ADMIN'))", connection);
        command.Parameters.AddWithValue("@id", userId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (reader.GetBoolean(0), reader.GetBoolean(1), reader.GetBoolean(2));
    }

    private static void AddProblemParameters(MySqlCommand command, string userId, bool admin, string search, string? status, string? clientId)
    {
        command.Parameters.AddWithValue("@id", userId); command.Parameters.AddWithValue("@admin", admin); command.Parameters.AddWithValue("@search", search); command.Parameters.AddWithValue("@pattern", $"%{search.Replace("%", "\\%").Replace("_", "\\_")}%"); command.Parameters.AddWithValue("@status", (object?)status ?? DBNull.Value); command.Parameters.AddWithValue("@client", (object?)clientId ?? DBNull.Value);
    }

    public async Task<object[]?> GetHelpTopics(string userId, CancellationToken ct)
    {
        if (ConnectionString is null) return null;
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        var roles = new HashSet<string>(StringComparer.Ordinal);
        await using (var command = new MySqlCommand("SELECT role_code FROM user_roles WHERE user_id=@id", connection))
        {
            command.Parameters.AddWithValue("@id", userId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) roles.Add(reader.GetString(0));
        }
        var staff = roles.Overlaps(["INTERN", "TECHNICIAN", "TEAM_LEAD", "MANAGER", "ADMIN"]);
        var lead = roles.Overlaps(["TEAM_LEAD", "MANAGER", "ADMIN"]);
        var admin = roles.Contains("ADMIN");
        if (!staff && !roles.Contains("USER")) return null;
        string? changeAccess;
        await using (var command = new MySqlCommand("SELECT change_access FROM users WHERE id=@id", connection))
        {
            command.Parameters.AddWithValue("@id", userId);
            changeAccess = Convert.ToString(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
        }
        var reportViewer = admin || await HasPermission(userId, "REPORTS_VIEW", null, ct);
        using var document = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "help-topics.json"), ct));
        return document.RootElement.EnumerateArray().Where(topic =>
        {
            var audience = topic.GetProperty("audience").GetString();
            var permission = topic.TryGetProperty("permission", out var requested) ? requested.GetString() : null;
            if (permission == "changeAccess" && !admin && (changeAccess is null or "NONE")) return false;
            if (permission == "REPORTS_VIEW" && !reportViewer) return false;
            return audience switch { "everyone" => true, "client" => roles.Contains("USER"), "staff" => staff, "lead" => lead, "admin" => admin, _ => false };
        }).Select(topic => (object)new
        {
            id = topic.GetProperty("id").GetString(),
            title = topic.GetProperty("title").GetString(),
            summary = topic.GetProperty("summary").GetString(),
            steps = topic.GetProperty("steps").EnumerateArray().Select(step => step.GetString()).ToArray(),
            href = topic.GetProperty("href").GetString()
        }).ToArray();
    }

    public async Task<object?> ListAdminResource(string userId,string resource,string search,string status,string? clientId,int page,int pageSize,CancellationToken ct)
    {
        var requiredPermission=resource switch{"clients"=>"CLIENTS_MANAGE","contacts"=>"CONTACTS_MANAGE","sites"=>"SITES_MANAGE","assets"=>"ASSETS_MANAGE","categories"=>"CATEGORY_MANAGE","tags"=>"TAGS_MANAGE","sources"=>"SOURCES_MANAGE","departments"=>"USERS_MANAGE","audit-logs"=>"AUDIT_VIEW",_=>null};
        var defaults=resource switch{"categories"=>new HashSet<string>(["TEAM_LEAD","MANAGER","ADMIN"],StringComparer.Ordinal),"clients" or "contacts" or "sites" or "assets" or "tags" or "sources" or "departments" or "audit-logs"=>new HashSet<string>(["ADMIN"],StringComparer.Ordinal),_=>null};
        if(resource=="users") {if(!await IsAdministrator(userId,ct))return null;var users=await ListUsers(ct);var filtered=users.Where(item=>(status=="all"||(item.TryGetValue("active",out var active)&&Convert.ToBoolean(active,System.Globalization.CultureInfo.InvariantCulture)==(status=="active")))&&(string.IsNullOrEmpty(search)||System.Text.Json.JsonSerializer.Serialize(item).Contains(search,StringComparison.OrdinalIgnoreCase))&&(clientId is null||System.Text.Json.JsonSerializer.Serialize(item.TryGetValue("client_ids",out var ids)?ids:null).Contains(clientId,StringComparison.OrdinalIgnoreCase))).ToArray();return new{total=filtered.Length,page,pageSize,items=filtered.Skip((page-1)*pageSize).Take(pageSize).ToArray()};}
        if(resource=="audit-logs") {if(!await IsAdministrator(userId,ct)&&!await HasPermission(userId,"AUDIT_VIEW",defaults,ct))return null;var logs=await ListAuditLogs(ct);var filtered=logs.Where(item=>(string.IsNullOrEmpty(search)||System.Text.Json.JsonSerializer.Serialize(item).Contains(search,StringComparison.OrdinalIgnoreCase))).ToArray();return new{total=filtered.Length,page,pageSize,items=filtered.Skip((page-1)*pageSize).Take(pageSize).ToArray()};}
        if(requiredPermission is null||ConnectionString is null)return null;
        if(!await IsAdministrator(userId,ct)&&!await HasPermission(userId,requiredPermission,defaults,ct))return null;
        var config=resource switch
        {
            "clients"=>("SELECT c.id,c.name,c.ticket_prefix,c.status,c.primary_email,c.primary_phone,c.notes,c.created_at,c.updated_at FROM clients c","CONCAT_WS(' ',r.name,r.ticket_prefix,r.status,r.primary_email,r.primary_phone,r.notes)","r.status='ACTIVE'","1=1","r.name,r.id"),
            "contacts"=>("SELECT x.id,x.client_id,x.site_id,x.user_id,x.name,x.email,x.phone,x.active,x.created_at,x.updated_at,c.name AS client_name FROM contacts x JOIN clients c ON c.id=x.client_id","CONCAT_WS(' ',r.name,r.email,r.phone,r.client_name)","r.active=1","r.client_id=@client","r.name,r.id"),
            "sites"=>("SELECT x.id,x.client_id,x.name,x.address,x.active,x.created_at,x.updated_at,c.name AS client_name FROM sites x JOIN clients c ON c.id=x.client_id","CONCAT_WS(' ',r.name,r.address,r.client_name)","r.active=1","r.client_id=@client","r.name,r.id"),
            "assets"=>("SELECT x.id,x.client_id,x.site_id,x.name,x.asset_type,x.serial_number,x.external_reference,x.active,x.created_at,x.updated_at,c.name AS client_name FROM assets x JOIN clients c ON c.id=x.client_id","CONCAT_WS(' ',r.name,r.asset_type,r.serial_number,r.external_reference,r.client_name)","r.active=1","r.client_id=@client","r.name,r.id"),
            "categories"=>("SELECT id,parent_id,name,description,active,created_at,updated_at FROM categories","CONCAT_WS(' ',r.name,r.description)","r.active=1","1=1","r.name,r.id"),
            "tags"=>("SELECT id,name,description,active,created_at,updated_at FROM tags","CONCAT_WS(' ',r.name,r.description)","r.active=1","1=1","r.name,r.id"),
            "sources"=>("SELECT code AS id,code,name,active,sort_order,created_at,updated_at FROM ticket_sources","CONCAT_WS(' ',r.code,r.name)","r.active=1","1=1","r.name,r.id"),
            "departments"=>("SELECT id,name,description,active,created_at,updated_at FROM departments","CONCAT_WS(' ',r.name,r.description)","r.active=1","1=1","r.name,r.id"),
            _=>((string Source,string SearchSql,string ActiveSql,string ClientSql,string Order)?)null
        };
        if(config is null)return null;
        var (source,searchSql,activeSql,clientSql,order)=config.Value;
        await using var connection=CreateConnection();await connection.OpenAsync(ct);
        var where=$"(@q='' OR {searchSql} LIKE @search) AND (@status='all' OR ({activeSql})=(@status='active')) AND (@client IS NULL OR {clientSql})";
        var sql=$"WITH records AS ({source}) SELECT (SELECT COUNT(*) FROM records r WHERE {where}) AS total,r.* FROM records r WHERE {where} ORDER BY {order} LIMIT @take OFFSET @skip";
        await using var command=new MySqlCommand(sql,connection);command.Parameters.AddWithValue("@q",search);command.Parameters.AddWithValue("@search",$"%{search.Replace("%","\\%").Replace("_","\\_")}%");command.Parameters.AddWithValue("@status",status);command.Parameters.AddWithValue("@client",(object?)clientId??DBNull.Value);command.Parameters.AddWithValue("@take",pageSize);command.Parameters.AddWithValue("@skip",(page-1)*pageSize);
        await using var reader=await command.ExecuteReaderAsync(ct);var items=new List<Dictionary<string,object?>>();var total=0;
        while(await reader.ReadAsync(ct)){total=Convert.ToInt32(reader.GetValue(0),System.Globalization.CultureInfo.InvariantCulture);var row=new Dictionary<string,object?>(StringComparer.Ordinal);for(var i=1;i<reader.FieldCount;i++)row[reader.GetName(i)]=ReadValue(reader,i);items.Add(row);}
        await reader.DisposeAsync();
        if(items.Count==0){await using var count=new MySqlCommand($"WITH records AS ({source}) SELECT COUNT(*) FROM records r WHERE {where}",connection);count.Parameters.AddWithValue("@q",search);count.Parameters.AddWithValue("@search",$"%{search.Replace("%","\\%").Replace("_","\\_")}%");count.Parameters.AddWithValue("@status",status);count.Parameters.AddWithValue("@client",(object?)clientId??DBNull.Value);total=Convert.ToInt32(await count.ExecuteScalarAsync(ct),System.Globalization.CultureInfo.InvariantCulture);}
        return new{total,page,pageSize,items};
    }

    public async Task<object?> GetTicketDashboard(string userId, bool requestedAll, CancellationToken ct)
    {
        if (ConnectionString is null) return null;
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        await using var grants = new MySqlCommand("SELECT EXISTS(SELECT 1 FROM user_roles WHERE user_id=@id AND role_code='ADMIN'),EXISTS(SELECT 1 FROM user_roles WHERE user_id=@id AND role_code IN ('MANAGER','TEAM_LEAD')),EXISTS(SELECT 1 FROM user_roles WHERE user_id=@id AND role_code IN ('TECHNICIAN','INTERN','MANAGER','TEAM_LEAD','ADMIN')),EXISTS(SELECT 1 FROM user_permissions WHERE user_id=@id AND permission_code='TICKETS_TEAM_VIEW')", connection);
        grants.Parameters.AddWithValue("@id", userId);
        await using var reader = await grants.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || !reader.GetBoolean(2)) return null;
        var isAdmin = reader.GetBoolean(0);
        var isWide = isAdmin || reader.GetBoolean(1);
        var canTeam = reader.GetBoolean(3) || isWide;
        await reader.DisposeAsync();
        if (!canTeam) canTeam = await HasDefaultTeamPermission(connection, userId, ct);
        if (requestedAll && !isWide && !canTeam) return null;
        var all = requestedAll && (isWide || canTeam);
        var assignment = all ? "1=1" : "t.assigned_user_id=@id";
        var access = isAdmin ? "1=1" : "(t.client_id IN (SELECT client_id FROM client_users WHERE user_id=@id) OR t.client_id='00000000-0000-4000-8000-000000000132' OR t.site_id IN (SELECT site_id FROM user_sites WHERE user_id=@id))";
        var filter = $"({assignment}) AND ({access})";
        var countsSql = $"""
            SELECT SUM(CASE WHEN t.status NOT IN ('RESOLVED','CLOSED','CANCELLED') THEN 1 ELSE 0 END) AS open,
                   SUM(CASE WHEN t.status='NEW' THEN 1 ELSE 0 END) AS new,
                   SUM(CASE WHEN t.status='IN_PROGRESS' THEN 1 ELSE 0 END) AS in_progress,
                   SUM(CASE WHEN t.status IN ('WAITING_FOR_CLIENT','WAITING_FOR_VENDOR') THEN 1 ELSE 0 END) AS waiting,
                   SUM(CASE WHEN t.status='RESOLVED' AND t.resolved_at>=UTC_TIMESTAMP(6)-INTERVAL 7 DAY THEN 1 ELSE 0 END) AS recently_resolved,
                   SUM(CASE WHEN t.approval_status='PENDING' THEN 1 ELSE 0 END) AS pending_approval,
                   SUM(CASE WHEN t.assigned_user_id IS NULL AND t.status NOT IN ('RESOLVED','CLOSED','CANCELLED') THEN 1 ELSE 0 END) AS unassigned,
                   SUM(CASE WHEN t.status NOT IN ('RESOLVED','CLOSED','CANCELLED') AND sla.paused_at IS NULL AND UTC_TIMESTAMP(6)>=sla.resolution_warning_at AND UTC_TIMESTAMP(6)<sla.resolution_due_at THEN 1 ELSE 0 END) AS sla_at_risk,
                   SUM(CASE WHEN t.status NOT IN ('RESOLVED','CLOSED','CANCELLED') AND sla.paused_at IS NULL AND UTC_TIMESTAMP(6)>=sla.resolution_due_at THEN 1 ELSE 0 END) AS sla_breached,
                   0 AS unattended
            FROM tickets t LEFT JOIN ticket_sla sla ON sla.ticket_id=t.id WHERE {filter}
            """;
        var counts = await ReadDashboardRows(connection, countsSql, userId, ct);
        if (counts.FirstOrDefault() is { } dashboardCounts)
        {
            var pendingResponse = await ReadDashboardRows(connection, $"SELECT t.created_at,sla.calendar_id FROM tickets t JOIN ticket_sla sla ON sla.ticket_id=t.id WHERE {filter} AND t.status NOT IN ('RESOLVED','CLOSED','CANCELLED') AND t.first_response_at IS NULL AND sla.paused_at IS NULL", userId, ct);
            var calendars = new Dictionary<string, SlaCalendar>(StringComparer.Ordinal);
            var now = DateTime.UtcNow;
            var unattended = 0;
            foreach (var pending in pendingResponse)
            {
                var calendarId = Convert.ToString(pending["calendar_id"], System.Globalization.CultureInfo.InvariantCulture)!;
                if (!calendars.TryGetValue(calendarId, out var calendar))
                {
                    calendar = await GetSlaCalendarById(connection, null, calendarId, ct);
                    if (calendar is null) continue;
                    calendars[calendarId] = calendar;
                }
                var created = DateTime.SpecifyKind(Convert.ToDateTime(pending["created_at"], System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);
                if (SlaBusinessCalendar.Between(calendar, created, now) >= 30) unattended++;
            }
            dashboardCounts["unattended"] = unattended;
        }
        var recent = await ReadDashboardRows(connection, $"SELECT t.id,t.ticket_number,t.client_id,t.requester_id,t.site_id,t.category_id,t.assigned_user_id,t.created_by_user_id,t.subject,t.description,t.source,t.status,t.priority,t.approval_status,t.created_at,t.updated_at,c.name AS client_name,r.name AS requester_name,r.email AS requester_email,s.name AS site_name,cat.name AS category_name,u.display_name AS assigned_name,CASE WHEN sla.resolution_due_at IS NULL THEN 'NOT_TRACKED' WHEN t.status='CANCELLED' THEN 'NOT_TRACKED' WHEN sla.resolution_completed_at IS NOT NULL AND sla.resolution_completed_at<=sla.resolution_due_at THEN 'MET' WHEN sla.paused_at IS NOT NULL THEN 'PAUSED' WHEN UTC_TIMESTAMP(6)>=sla.resolution_due_at THEN 'BREACHED' WHEN UTC_TIMESTAMP(6)>=sla.resolution_warning_at THEN 'AT_RISK' ELSE 'ON_TRACK' END AS sla_status FROM tickets t JOIN clients c ON c.id=t.client_id JOIN contacts r ON r.id=t.requester_id LEFT JOIN sites s ON s.id=t.site_id LEFT JOIN categories cat ON cat.id=t.category_id LEFT JOIN users u ON u.id=t.assigned_user_id LEFT JOIN ticket_sla sla ON sla.ticket_id=t.id WHERE {filter} ORDER BY t.updated_at DESC LIMIT 8", userId, ct);
        var status = await ReadDashboardRows(connection, $"SELECT t.status AS label,COUNT(*) AS value FROM tickets t WHERE {filter} GROUP BY t.status ORDER BY COUNT(*) DESC", userId, ct);
        var priority = await ReadDashboardRows(connection, $"SELECT t.priority AS label,COUNT(*) AS value FROM tickets t WHERE {filter} GROUP BY t.priority ORDER BY COUNT(*) DESC", userId, ct);
        var source = await ReadDashboardRows(connection, $"SELECT COALESCE(ts.name,t.source) AS label,COUNT(*) AS value FROM tickets t LEFT JOIN ticket_sources ts ON ts.code=t.source WHERE {filter} GROUP BY COALESCE(ts.name,t.source) ORDER BY COUNT(*) DESC", userId, ct);
        var trend = await ReadDashboardRows(connection, $"WITH RECURSIVE days(day) AS (SELECT CURRENT_DATE-INTERVAL 13 DAY UNION ALL SELECT day+INTERVAL 1 DAY FROM days WHERE day<CURRENT_DATE) SELECT DATE_FORMAT(days.day,'%Y-%m-%d') AS day, (SELECT COUNT(*) FROM tickets t WHERE {filter} AND t.created_at>=days.day AND t.created_at<days.day+INTERVAL 1 DAY) AS created, (SELECT COUNT(*) FROM tickets t WHERE {filter} AND t.resolved_at>=days.day AND t.resolved_at<days.day+INTERVAL 1 DAY) AS resolved FROM days ORDER BY days.day", userId, ct);
        var workload = all ? await ReadDashboardRows(connection, $"SELECT u.display_name AS name,SUM(CASE WHEN t.status NOT IN ('RESOLVED','CLOSED','CANCELLED') THEN 1 ELSE 0 END) AS open,SUM(CASE WHEN t.status='RESOLVED' AND t.resolved_at>=UTC_TIMESTAMP(6)-INTERVAL 30 DAY THEN 1 ELSE 0 END) AS resolved FROM users u JOIN user_roles ur ON ur.user_id=u.id AND ur.role_code IN ('TECHNICIAN','INTERN','TEAM_LEAD','MANAGER','ADMIN') LEFT JOIN tickets t ON t.assigned_user_id=u.id AND ({access}) WHERE u.active GROUP BY u.id,u.display_name ORDER BY open DESC,u.display_name", userId, ct) : [];
        return new { counts = counts.FirstOrDefault() ?? new Dictionary<string, object?>(), recent, charts = new { status, priority, source, trend, workload } };
    }

    public async Task<List<Dictionary<string, object?>>> GetSavedTicketViews(string userId, CancellationToken ct)
    {
        if (ConnectionString is null) return [];
        await using var connection = CreateConnection(); await connection.OpenAsync(ct);
        await using var command = new MySqlCommand("SELECT id,name,query,created_at,updated_at FROM saved_ticket_views WHERE user_id=@id ORDER BY LOWER(name),id", connection); command.Parameters.AddWithValue("@id", userId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync(ct)) { var row = new Dictionary<string, object?>(StringComparer.Ordinal); for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = ReadValue(reader, i); rows.Add(row); }
        return rows;
    }

    public async Task<object> SaveTicketView(string userId, SaveTicketViewRequest body, CancellationToken ct)
    {
        var name = body.Name.Trim();
        if (name.Length is < 2 or > 80 || body.Query is null) throw new ArgumentException("Enter a view name and filters.");
        var allowed = new HashSet<string>(["q", "dashboardFilter", "status", "priority", "clientId", "siteId", "assignedUserId", "categoryId", "tagId", "source", "slaStatus", "from", "to"], StringComparer.Ordinal);
        if (body.Query.Keys.Any(key => !allowed.Contains(key))) throw new ArgumentException("The saved view contains an unsupported filter.");
        if (body.Query.Any(item => item.Value is null || item.Value!.Length > 200)) throw new ArgumentException("A saved filter value is too long.");
        if (ConnectionString is null) throw new InvalidOperationException("Database is not configured.");
        var queryJson = System.Text.Json.JsonSerializer.Serialize(body.Query.Where(item => !string.IsNullOrWhiteSpace(item.Value)).ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal));
        await using var connection = CreateConnection(); await connection.OpenAsync(ct);
        await using (var save = new MySqlCommand("INSERT INTO saved_ticket_views(id,user_id,name,query) VALUES(@id,@user,@name,CAST(@query AS JSON)) ON DUPLICATE KEY UPDATE query=VALUES(query),updated_at=UTC_TIMESTAMP(6)", connection))
        {
            save.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("D")); save.Parameters.AddWithValue("@user", userId); save.Parameters.AddWithValue("@name", name); save.Parameters.AddWithValue("@query", queryJson); await save.ExecuteNonQueryAsync(ct);
        }
        string id;
        await using (var select = new MySqlCommand("SELECT id FROM saved_ticket_views WHERE user_id=@user AND LOWER(name)=LOWER(@name)", connection)) { select.Parameters.AddWithValue("@user", userId); select.Parameters.AddWithValue("@name", name); id = Convert.ToString(await select.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture)!; }
        return new { id, name, query = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string?>>(queryJson) };
    }

    public async Task<bool> DeleteSavedTicketView(string userId, string viewId, CancellationToken ct)
    {
        if (ConnectionString is null) return false;
        await using var connection = CreateConnection(); await connection.OpenAsync(ct);
        await using var command = new MySqlCommand("DELETE FROM saved_ticket_views WHERE id=@id AND user_id=@user", connection); command.Parameters.AddWithValue("@id", viewId); command.Parameters.AddWithValue("@user", userId);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<CreatedRequester?> CreateRequester(string userId, CreateRequesterRequest body, CancellationToken ct)
    {
        if (ConnectionString is null || !Guid.TryParse(body.ClientId, out _) || body.Name is null) return null;
        var name = body.Name.Trim();
        var email = body.Email?.Trim();
        var phone = body.Phone?.Trim();
        if (name.Length is < 2 or > 200 || (email?.Length ?? 0) > 255 || (phone?.Length ?? 0) > 50 ||
            (string.IsNullOrEmpty(email) && string.IsNullOrEmpty(phone))) throw new ArgumentException("Enter a requester name and an email address or phone number.");
        if (!string.IsNullOrEmpty(email))
        {
            try { _ = new System.Net.Mail.MailAddress(email); }
            catch (FormatException) { throw new ArgumentException("Enter a valid email address."); }
        }
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        await using var grants = new MySqlCommand("SELECT EXISTS(SELECT 1 FROM user_roles WHERE user_id=@id AND role_code='ADMIN'),EXISTS(SELECT 1 FROM user_roles WHERE user_id=@id AND role_code IN ('TECHNICIAN','INTERN','TEAM_LEAD','MANAGER','ADMIN'))", connection);
        grants.Parameters.AddWithValue("@id", userId);
        await using var grantReader = await grants.ExecuteReaderAsync(ct);
        if (!await grantReader.ReadAsync(ct)) return null;
        var isAdmin = grantReader.GetBoolean(0);
        var isStaff = grantReader.GetBoolean(1);
        await grantReader.DisposeAsync();
        if (!isStaff) return null;

        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var client = new MySqlCommand("SELECT c.id FROM clients c WHERE c.id=@client AND c.status='ACTIVE' AND (@admin=1 OR EXISTS(SELECT 1 FROM client_users cu WHERE cu.client_id=c.id AND cu.user_id=@id)) FOR UPDATE", connection, transaction);
        client.Parameters.AddWithValue("@client", body.ClientId);
        client.Parameters.AddWithValue("@admin", isAdmin);
        client.Parameters.AddWithValue("@id", userId);
        if (await client.ExecuteScalarAsync(ct) is null) return null;
        if (!string.IsNullOrEmpty(email))
        {
            await using var duplicate = new MySqlCommand("SELECT 1 FROM contacts WHERE client_id=@client AND LOWER(email)=LOWER(@email) AND active LIMIT 1", connection, transaction);
            duplicate.Parameters.AddWithValue("@client", body.ClientId);
            duplicate.Parameters.AddWithValue("@email", email);
            if (await duplicate.ExecuteScalarAsync(ct) is not null) throw new ArgumentException("A requester with this email already exists.");
        }
        var id = Guid.NewGuid().ToString("D");
        await using (var insert = new MySqlCommand("INSERT INTO contacts(id,client_id,name,email,phone,active) VALUES(@id,@client,@name,@email,@phone,1)", connection, transaction))
        {
            insert.Parameters.AddWithValue("@id", id);
            insert.Parameters.AddWithValue("@client", body.ClientId);
            insert.Parameters.AddWithValue("@name", name);
            insert.Parameters.AddWithValue("@email", (object?)email ?? DBNull.Value);
            insert.Parameters.AddWithValue("@phone", (object?)phone ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync(ct);
        }
        var detail = System.Text.Json.JsonSerializer.Serialize(new { clientId = body.ClientId, name });
        await using (var audit = new MySqlCommand("INSERT INTO audit_logs(actor_user_id,action,entity_type,entity_id,details) VALUES(@actor,'CONTACT.CREATED','CONTACT',@entity,CAST(@details AS JSON))", connection, transaction))
        {
            audit.Parameters.AddWithValue("@actor", userId);
            audit.Parameters.AddWithValue("@entity", id);
            audit.Parameters.AddWithValue("@details", detail);
            await audit.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return new CreatedRequester(id, body.ClientId, name, email);
    }

    public async Task<CreatedTicket> CreateTicket(string userId, CreateTicketRequest body, CancellationToken ct)
    {
        if (ConnectionString is null) throw new InvalidOperationException("Database is not configured.");
        if (!Guid.TryParse(body.ClientId, out _) || !Guid.TryParse(body.RequesterId, out _) || !Guid.TryParse(body.CategoryId, out _) ||
            (body.SiteId is not null && !Guid.TryParse(body.SiteId, out _)) || (body.DepartmentId is not null && !Guid.TryParse(body.DepartmentId, out _)) ||
            (body.AssignedUserId is not null && !Guid.TryParse(body.AssignedUserId, out _))) throw new ArgumentException("Select valid ticket options.");
        var subject = body.Subject?.Trim() ?? "";
        var description = body.Description?.Trim() ?? "";
        if (subject.Length is < 3 or > 300 || description.Length is < 3 or > 20000) throw new ArgumentException("Enter a subject and description.");
        if (body.Source is null || body.Source.Length > 50 || !System.Text.RegularExpressions.Regex.IsMatch(body.Source, "^[A-Z0-9_]+$")) throw new ArgumentException("Select a valid ticket source.");
        if (body.Priority is not ("P1_CRITICAL" or "P2_HIGH" or "P3_MEDIUM" or "P4_LOW")) throw new ArgumentException("Select a valid priority.");
        if ((body.TagIds?.Length ?? 0) > 30 || (body.Attachments?.Length ?? 0) > 4) throw new ArgumentException("Too many tags or attachments.");
        var tagIds = (body.TagIds ?? []).Distinct(StringComparer.Ordinal).ToArray();
        if (tagIds.Any(id => !Guid.TryParse(id, out _))) throw new ArgumentException("Select valid ticket tags.");
        var files = new List<(CreateAttachment File, byte[] Bytes)>();
        foreach (var file in body.Attachments ?? [])
        {
            var allowedTypes = new HashSet<string>(StringComparer.Ordinal) { "application/pdf", "application/zip", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "image/gif", "image/jpeg", "image/png", "text/csv", "text/plain" };
            if (string.IsNullOrWhiteSpace(file.FileName) || file.FileName.Length > 255 || file.FileName.Contains('/') || file.FileName.Contains('\\') || file.FileName.Contains('\0') || !allowedTypes.Contains(file.ContentType) || file.ContentBase64.Length > 7_000_000 || file.Visibility is not (null or "PUBLIC" or "INTERNAL")) throw new ArgumentException("An attachment is invalid or its file type is not allowed.");
            byte[] bytes;
            try { bytes = Convert.FromBase64String(file.ContentBase64); } catch (FormatException) { throw new ArgumentException("An attachment could not be read."); }
            if (bytes.Length is 0 or > 5 * 1024 * 1024 || Convert.ToBase64String(bytes).TrimEnd('=') != file.ContentBase64.TrimEnd('=')) throw new ArgumentException("Attachments must contain valid data between 1 byte and 5 MB.");
            files.Add((file, bytes));
        }
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            await using var grant = new MySqlCommand("SELECT EXISTS(SELECT 1 FROM user_roles WHERE user_id=@id AND role_code='ADMIN'),EXISTS(SELECT 1 FROM user_roles WHERE user_id=@id AND role_code IN ('TECHNICIAN','INTERN','TEAM_LEAD','MANAGER','ADMIN')),EXISTS(SELECT 1 FROM client_users WHERE user_id=@id AND client_id=@client),EXISTS(SELECT 1 FROM user_sites WHERE user_id=@id AND site_id=@site),EXISTS(SELECT 1 FROM user_sites WHERE user_id=@id),work_scope FROM users WHERE id=@id AND active=1 AND (access_expires_at IS NULL OR access_expires_at>UTC_TIMESTAMP(6))", connection, transaction);
            grant.Parameters.AddWithValue("@id", userId); grant.Parameters.AddWithValue("@client", body.ClientId); grant.Parameters.AddWithValue("@site", (object?)body.SiteId ?? DBNull.Value);
            await using var grantReader = await grant.ExecuteReaderAsync(ct);
            if (!await grantReader.ReadAsync(ct) || !grantReader.GetBoolean(1)) throw new UnauthorizedAccessException();
            var admin = grantReader.GetBoolean(0); var hasClient = grantReader.GetBoolean(2); var hasSite = grantReader.GetBoolean(3); var hasSites = grantReader.GetBoolean(4); var workScope = grantReader.GetString(5);
            await grantReader.DisposeAsync();
            if (!admin && !hasClient) throw new UnauthorizedAccessException();
            var internalClient = body.ClientId == "00000000-0000-4000-8000-000000000132";
            if (body.SiteId is not null && !admin && hasSites && !hasSite) throw new UnauthorizedAccessException();
            if (internalClient && workScope is not ("INTERNAL" or "BOTH" or "ALL") && !admin) throw new UnauthorizedAccessException();
            await using (var valid = new MySqlCommand("SELECT EXISTS(SELECT 1 FROM clients WHERE id=@client AND status='ACTIVE'),EXISTS(SELECT 1 FROM contacts WHERE id=@requester AND client_id=@client AND active),EXISTS(SELECT 1 FROM categories WHERE id=@category AND active),EXISTS(SELECT 1 FROM ticket_sources WHERE code=@source AND active),IF(@site IS NULL,1,EXISTS(SELECT 1 FROM sites WHERE id=@site AND client_id=@client AND active)),IF(@department IS NULL,1,EXISTS(SELECT 1 FROM departments WHERE id=@department AND active))", connection, transaction))
            {
                valid.Parameters.AddWithValue("@client", body.ClientId); valid.Parameters.AddWithValue("@requester", body.RequesterId); valid.Parameters.AddWithValue("@category", body.CategoryId); valid.Parameters.AddWithValue("@source", body.Source); valid.Parameters.AddWithValue("@site", (object?)body.SiteId ?? DBNull.Value); valid.Parameters.AddWithValue("@department", (object?)body.DepartmentId ?? DBNull.Value);
                await using var vr = await valid.ExecuteReaderAsync(ct); await vr.ReadAsync(ct);
                if (!vr.GetBoolean(0) || !vr.GetBoolean(1) || !vr.GetBoolean(2) || !vr.GetBoolean(3) || !vr.GetBoolean(4)) throw new ArgumentException("One or more selected ticket options are no longer available.");
                if (internalClient != (body.DepartmentId is not null) || !vr.GetBoolean(5)) throw new ArgumentException("Internal tickets need an active department; other tickets cannot use one.");
            }
            foreach (var tag in tagIds)
            {
                await using var tagCheck = new MySqlCommand("SELECT 1 FROM tags WHERE id=@id AND active", connection, transaction); tagCheck.Parameters.AddWithValue("@id", tag);
                if (await tagCheck.ExecuteScalarAsync(ct) is null) throw new ArgumentException("A selected tag is unavailable.");
            }
            if (body.AssignedUserId is not null)
            {
                await using var assignee = new MySqlCommand("SELECT EXISTS(SELECT 1 FROM user_roles ur JOIN users u ON u.id=ur.user_id WHERE ur.user_id=@assignee AND u.active AND ur.role_code IN ('TECHNICIAN','INTERN','TEAM_LEAD','MANAGER','ADMIN') AND (ur.role_code='ADMIN' OR EXISTS(SELECT 1 FROM client_users cu WHERE cu.user_id=ur.user_id AND cu.client_id=@client)))", connection, transaction);
                assignee.Parameters.AddWithValue("@assignee", body.AssignedUserId); assignee.Parameters.AddWithValue("@client", body.ClientId);
                if (Convert.ToInt32(await assignee.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture) != 1) throw new ArgumentException("The assigned person is not active service-desk staff for this client.");
            }
            var ticketNumber = await AllocateTicketNumber(connection, transaction, body.ClientId, body.SiteId, ct);
            var id = Guid.NewGuid().ToString("D");
            await using (var insert = new MySqlCommand("INSERT INTO tickets(id,ticket_number,client_id,requester_id,site_id,department_id,location_text,category_id,assigned_user_id,created_by_user_id,subject,description,source,priority,source_reference_id) VALUES(@id,@number,@client,@requester,@site,@department,@location,@category,@assignee,@actor,@subject,@description,@source,@priority,@reference)", connection, transaction))
            {
                insert.Parameters.AddWithValue("@id", id); insert.Parameters.AddWithValue("@number", ticketNumber); insert.Parameters.AddWithValue("@client", body.ClientId); insert.Parameters.AddWithValue("@requester", body.RequesterId); insert.Parameters.AddWithValue("@site", (object?)body.SiteId ?? DBNull.Value); insert.Parameters.AddWithValue("@department", (object?)body.DepartmentId ?? DBNull.Value); insert.Parameters.AddWithValue("@location", string.IsNullOrWhiteSpace(body.LocationText) ? DBNull.Value : body.LocationText.Trim()); insert.Parameters.AddWithValue("@category", body.CategoryId); insert.Parameters.AddWithValue("@assignee", (object?)body.AssignedUserId ?? DBNull.Value); insert.Parameters.AddWithValue("@actor", userId); insert.Parameters.AddWithValue("@subject", subject); insert.Parameters.AddWithValue("@description", description); insert.Parameters.AddWithValue("@source", body.Source); insert.Parameters.AddWithValue("@priority", body.Priority); insert.Parameters.AddWithValue("@reference", string.IsNullOrWhiteSpace(body.SourceReferenceId) ? DBNull.Value : body.SourceReferenceId.Trim());
                await insert.ExecuteNonQueryAsync(ct);
            }
            foreach (var tag in tagIds) { await using var link = new MySqlCommand("INSERT INTO ticket_tags(ticket_id,tag_id,added_by_user_id) VALUES(@ticket,@tag,@actor)", connection, transaction); link.Parameters.AddWithValue("@ticket", id); link.Parameters.AddWithValue("@tag", tag); link.Parameters.AddWithValue("@actor", userId); await link.ExecuteNonQueryAsync(ct); }
            if (body.AssignedUserId is not null) { await using var assignment = new MySqlCommand("INSERT INTO ticket_assignments(ticket_id,assigned_user_id,actor_user_id,reason) VALUES(@ticket,@assignee,@actor,'Initial assignment')", connection, transaction); assignment.Parameters.AddWithValue("@ticket", id); assignment.Parameters.AddWithValue("@assignee", body.AssignedUserId); assignment.Parameters.AddWithValue("@actor", userId); await assignment.ExecuteNonQueryAsync(ct); }
            foreach (var (file, bytes) in files)
            {
                var fileId = Guid.NewGuid().ToString("D"); var storageKey = $"mysql/{id}/{fileId}"; var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                await using var attachment = new MySqlCommand("INSERT INTO ticket_attachments(id,ticket_id,uploaded_by_user_id,file_name,content_type,size_bytes,storage_key,sha256,content,visibility) VALUES(@id,@ticket,@actor,@name,@type,@size,@key,@hash,@content,@visibility)", connection, transaction);
                attachment.Parameters.AddWithValue("@id", fileId); attachment.Parameters.AddWithValue("@ticket", id); attachment.Parameters.AddWithValue("@actor", userId); attachment.Parameters.AddWithValue("@name", Path.GetFileName(file.FileName)); attachment.Parameters.AddWithValue("@type", file.ContentType); attachment.Parameters.AddWithValue("@size", bytes.Length); attachment.Parameters.AddWithValue("@key", storageKey); attachment.Parameters.AddWithValue("@hash", hash); attachment.Parameters.Add("@content", MySqlDbType.LongBlob).Value = bytes; attachment.Parameters.AddWithValue("@visibility", file.Visibility ?? "INTERNAL"); await attachment.ExecuteNonQueryAsync(ct);
            }
            var calendar = await GetSlaCalendar(connection, transaction, body.Priority, ct);
            if (calendar is not null)
            {
                var createdAt = DateTime.SpecifyKind(Convert.ToDateTime(await new MySqlCommand("SELECT UTC_TIMESTAMP(6)", connection, transaction).ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);
                var responseWarning = SlaBusinessCalendar.Add(calendar, createdAt, (int)Math.Ceiling(calendar.ResponseMinutes * calendar.WarningPercent / 100.0));
                var responseDue = SlaBusinessCalendar.Add(calendar, createdAt, calendar.ResponseMinutes);
                var resolutionWarning = SlaBusinessCalendar.Add(calendar, createdAt, (int)Math.Ceiling(calendar.ResolutionMinutes * calendar.WarningPercent / 100.0));
                var resolutionDue = SlaBusinessCalendar.Add(calendar, createdAt, calendar.ResolutionMinutes);
                await using var sla = new MySqlCommand("INSERT INTO ticket_sla(ticket_id,policy_id,calendar_id,target_priority,response_warning_at,response_due_at,resolution_warning_at,resolution_due_at) VALUES(@ticket,@policy,@calendar,@priority,@rw,@rd,@sw,@sd)", connection, transaction);
                sla.Parameters.AddWithValue("@ticket", id); sla.Parameters.AddWithValue("@policy", calendar.PolicyId); sla.Parameters.AddWithValue("@calendar", calendar.Id); sla.Parameters.AddWithValue("@priority", body.Priority); sla.Parameters.AddWithValue("@rw", responseWarning); sla.Parameters.AddWithValue("@rd", responseDue); sla.Parameters.AddWithValue("@sw", resolutionWarning); sla.Parameters.AddWithValue("@sd", resolutionDue); await sla.ExecuteNonQueryAsync(ct);
            }
            await using (var history = new MySqlCommand("INSERT INTO ticket_history(ticket_id,actor_user_id,action,new_value) VALUES(@ticket,@actor,'TICKET.CREATED',CAST(@value AS JSON))", connection, transaction)) { history.Parameters.AddWithValue("@ticket", id); history.Parameters.AddWithValue("@actor", userId); history.Parameters.AddWithValue("@value", System.Text.Json.JsonSerializer.Serialize(new { ticketNumber, source = body.Source })); await history.ExecuteNonQueryAsync(ct); }
            await using (var audit = new MySqlCommand("INSERT INTO audit_logs(actor_user_id,action,entity_type,entity_id,details) VALUES(@actor,'TICKET.CREATED','TICKET',@ticket,CAST(@details AS JSON))", connection, transaction)) { audit.Parameters.AddWithValue("@actor", userId); audit.Parameters.AddWithValue("@ticket", id); audit.Parameters.AddWithValue("@details", System.Text.Json.JsonSerializer.Serialize(new { ticketNumber, clientId = body.ClientId })); await audit.ExecuteNonQueryAsync(ct); }
            await QueueTicketEvent(connection,transaction,userId,id,null,"Ticket created",ct);
            await transaction.CommitAsync(ct);
            return new CreatedTicket(id, ticketNumber);
        }
        catch { await transaction.RollbackAsync(ct); throw; }
    }

    public async Task<CreatedTicketComment?> CreateTicketComment(string userId, string ticketId, CreateTicketCommentRequest body, CancellationToken ct)
    {
        if (ConnectionString is null) return null;
        var detail = await GetTicketDetail(userId, ticketId, ct);
        if (detail is null) return null;
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            await using var grant = new MySqlCommand("SELECT EXISTS(SELECT 1 FROM user_roles WHERE user_id=@id AND role_code='INTERN'),EXISTS(SELECT 1 FROM user_roles WHERE user_id=@id AND role_code IN ('TECHNICIAN','INTERN','TEAM_LEAD','MANAGER','ADMIN'))", connection, transaction);
            grant.Parameters.AddWithValue("@id", userId);
            await using var reader = await grant.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct) || !reader.GetBoolean(1)) throw new UnauthorizedAccessException();
            var isIntern = reader.GetBoolean(0);
            await reader.DisposeAsync();
            if (isIntern && body.Type == "PUBLIC") throw new UnauthorizedAccessException();

            var commentId = Guid.NewGuid().ToString("D");
            var commentBody = body.Body.Trim();
            await using (var insert = new MySqlCommand("INSERT INTO ticket_comments(id,ticket_id,author_user_id,comment_type,body) VALUES(@id,@ticket,@actor,@type,@body)", connection, transaction))
            {
                insert.Parameters.AddWithValue("@id", commentId); insert.Parameters.AddWithValue("@ticket", ticketId); insert.Parameters.AddWithValue("@actor", userId); insert.Parameters.AddWithValue("@type", body.Type); insert.Parameters.AddWithValue("@body", commentBody); await insert.ExecuteNonQueryAsync(ct);
            }
            if (body.Type == "PUBLIC")
            {
                await using var update = new MySqlCommand("UPDATE tickets SET first_response_at=COALESCE(first_response_at,UTC_TIMESTAMP(6)),updated_at=UTC_TIMESTAMP(6) WHERE id=@ticket", connection, transaction);
                update.Parameters.AddWithValue("@ticket", ticketId); await update.ExecuteNonQueryAsync(ct);
                await using var sla = new MySqlCommand("UPDATE ticket_sla SET response_completed_at=COALESCE(response_completed_at,UTC_TIMESTAMP(6)),updated_at=UTC_TIMESTAMP(6) WHERE ticket_id=@ticket", connection, transaction); sla.Parameters.AddWithValue("@ticket", ticketId); await sla.ExecuteNonQueryAsync(ct);
            }
            else
            {
                await using var update = new MySqlCommand("UPDATE tickets SET updated_at=UTC_TIMESTAMP(6) WHERE id=@ticket", connection, transaction); update.Parameters.AddWithValue("@ticket", ticketId); await update.ExecuteNonQueryAsync(ct);
            }
            var action = body.Type == "PUBLIC" ? "TICKET.PUBLIC_REPLY_ADDED" : "TICKET.INTERNAL_NOTE_ADDED";
            var metadata = System.Text.Json.JsonSerializer.Serialize(new { commentId });
            await using (var history = new MySqlCommand("INSERT INTO ticket_history(ticket_id,actor_user_id,action,new_value) VALUES(@ticket,@actor,@action,CAST(@value AS JSON))", connection, transaction)) { history.Parameters.AddWithValue("@ticket", ticketId); history.Parameters.AddWithValue("@actor", userId); history.Parameters.AddWithValue("@action", action); history.Parameters.AddWithValue("@value", metadata); await history.ExecuteNonQueryAsync(ct); }
            await using (var audit = new MySqlCommand("INSERT INTO audit_logs(actor_user_id,action,entity_type,entity_id,details) VALUES(@actor,@action,'TICKET',@ticket,CAST(@details AS JSON))", connection, transaction)) { audit.Parameters.AddWithValue("@actor", userId); audit.Parameters.AddWithValue("@action", action); audit.Parameters.AddWithValue("@ticket", ticketId); audit.Parameters.AddWithValue("@details", metadata); await audit.ExecuteNonQueryAsync(ct); }
            if(body.Type=="PUBLIC")await QueueTicketEvent(connection,transaction,userId,ticketId,commentId,"Public reply added",ct);
            await transaction.CommitAsync(ct);
            return new CreatedTicketComment(commentId, body.Type, commentBody);
        }
        catch { await transaction.RollbackAsync(ct); throw; }
    }

    public async Task<AssignedTicket?> AssignTicket(string userId, string ticketId, AssignTicketRequest body, CancellationToken ct)
    {
        if (ConnectionString is null) return null;
        if (await GetTicketDetail(userId, ticketId, ct) is null) return null;
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            await using var grants = new MySqlCommand("SELECT EXISTS(SELECT 1 FROM user_roles WHERE user_id=@id AND role_code='ADMIN'),EXISTS(SELECT 1 FROM user_roles WHERE user_id=@id AND role_code IN ('TECHNICIAN','TEAM_LEAD','MANAGER','ADMIN')),EXISTS(SELECT 1 FROM user_permissions WHERE user_id=@id AND permission_code='TICKETS_ASSIGN'),(EXISTS(SELECT 1 FROM user_roles ur LEFT JOIN role_access_policies rp ON rp.role_code=ur.role_code WHERE ur.user_id=@id AND ur.role_code IN ('TECHNICIAN','TEAM_LEAD','MANAGER','ADMIN') AND (rp.role_code IS NULL OR JSON_CONTAINS(rp.permissions,JSON_QUOTE('TICKETS_ASSIGN')))) OR EXISTS(SELECT 1 FROM staff_group_members gm JOIN access_groups g ON g.id=gm.group_id WHERE gm.user_id=@id AND g.active=1 AND g.kind='STAFF' AND JSON_CONTAINS(g.permissions,JSON_QUOTE('TICKETS_ASSIGN')))),EXISTS(SELECT 1 FROM user_access_policies p WHERE p.user_id=@id AND JSON_CONTAINS(p.allow_permissions,JSON_QUOTE('TICKETS_ASSIGN'))),EXISTS(SELECT 1 FROM user_access_policies p WHERE p.user_id=@id AND JSON_CONTAINS(p.deny_permissions,JSON_QUOTE('TICKETS_ASSIGN'))) ", connection, transaction);
            grants.Parameters.AddWithValue("@id", userId);
            await using var grantReader = await grants.ExecuteReaderAsync(ct);
            if (!await grantReader.ReadAsync(ct) || !grantReader.GetBoolean(1)) throw new UnauthorizedAccessException();
            var isAdmin = grantReader.GetBoolean(0);
            var canAssign = isAdmin || (grantReader.GetBoolean(2) || grantReader.GetBoolean(3) || grantReader.GetBoolean(4)) && !grantReader.GetBoolean(5);
            await grantReader.DisposeAsync();
            if (!canAssign) throw new UnauthorizedAccessException();
            await using var ticket = new MySqlCommand("SELECT client_id,site_id,assigned_user_id,ticket_number,subject FROM tickets WHERE id=@ticket FOR UPDATE", connection, transaction);
            ticket.Parameters.AddWithValue("@ticket", ticketId);
            await using var ticketReader = await ticket.ExecuteReaderAsync(ct);
            if (!await ticketReader.ReadAsync(ct)) return null;
            var clientId = ticketReader.GetString(0); var siteId = ticketReader.IsDBNull(1) ? null : ticketReader.GetString(1); var previousId = ticketReader.IsDBNull(2) ? null : ticketReader.GetString(2);
            await ticketReader.DisposeAsync();
            await using var target = new MySqlCommand("SELECT EXISTS(SELECT 1 FROM users u JOIN user_roles r ON r.user_id=u.id WHERE u.id=@target AND u.active AND (u.access_expires_at IS NULL OR u.access_expires_at>UTC_TIMESTAMP(6)) AND r.role_code IN ('INTERN','TECHNICIAN','TEAM_LEAD','MANAGER','ADMIN') AND (r.role_code='ADMIN' OR EXISTS(SELECT 1 FROM client_users cu WHERE cu.user_id=u.id AND cu.client_id=@client)) AND (@site IS NULL OR r.role_code='ADMIN' OR NOT EXISTS(SELECT 1 FROM user_sites us WHERE us.user_id=u.id) OR EXISTS(SELECT 1 FROM user_sites us WHERE us.user_id=u.id AND us.site_id=@site)))", connection, transaction);
            target.Parameters.AddWithValue("@target", body.AssignedUserId); target.Parameters.AddWithValue("@client", clientId); target.Parameters.AddWithValue("@site", (object?)siteId ?? DBNull.Value);
            if (Convert.ToInt32(await target.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture) != 1) throw new ArgumentException("That team member cannot access this client or site.");
            if (previousId == body.AssignedUserId) { await transaction.CommitAsync(ct); return new AssignedTicket(ticketId, body.AssignedUserId); }
            var reason = string.IsNullOrWhiteSpace(body.Reason) ? "Assigned by service desk" : body.Reason.Trim();
            await using (var update = new MySqlCommand("UPDATE tickets SET assigned_user_id=@target,updated_at=UTC_TIMESTAMP(6) WHERE id=@ticket", connection, transaction)) { update.Parameters.AddWithValue("@target", body.AssignedUserId); update.Parameters.AddWithValue("@ticket", ticketId); await update.ExecuteNonQueryAsync(ct); }
            await using (var history = new MySqlCommand("INSERT INTO ticket_history(ticket_id,actor_user_id,action,previous_value,new_value,reason) VALUES(@ticket,@actor,'TICKET.ASSIGNED',CAST(@previous AS JSON),CAST(@next AS JSON),@reason)", connection, transaction)) { history.Parameters.AddWithValue("@ticket", ticketId); history.Parameters.AddWithValue("@actor", userId); history.Parameters.AddWithValue("@previous", previousId is null ? "null" : System.Text.Json.JsonSerializer.Serialize(previousId)); history.Parameters.AddWithValue("@next", System.Text.Json.JsonSerializer.Serialize(body.AssignedUserId)); history.Parameters.AddWithValue("@reason", reason); await history.ExecuteNonQueryAsync(ct); }
            await using (var assignment = new MySqlCommand("INSERT INTO ticket_assignments(ticket_id,assigned_user_id,actor_user_id,reason) VALUES(@ticket,@target,@actor,@reason)", connection, transaction)) { assignment.Parameters.AddWithValue("@ticket", ticketId); assignment.Parameters.AddWithValue("@target", body.AssignedUserId); assignment.Parameters.AddWithValue("@actor", userId); assignment.Parameters.AddWithValue("@reason", reason); await assignment.ExecuteNonQueryAsync(ct); }
            await using (var audit = new MySqlCommand("INSERT INTO audit_logs(actor_user_id,action,entity_type,entity_id,details) VALUES(@actor,'TICKET.ASSIGNED','TICKET',@ticket,CAST(@details AS JSON))", connection, transaction)) { audit.Parameters.AddWithValue("@actor", userId); audit.Parameters.AddWithValue("@ticket", ticketId); audit.Parameters.AddWithValue("@details", System.Text.Json.JsonSerializer.Serialize(new { assignedUserId = body.AssignedUserId, reason })); await audit.ExecuteNonQueryAsync(ct); }
            await QueueTicketEvent(connection,transaction,userId,ticketId,null,"Ticket assigned",ct);
            await transaction.CommitAsync(ct);
            return new AssignedTicket(ticketId, body.AssignedUserId);
        }
        catch { await transaction.RollbackAsync(ct); throw; }
    }

    public async Task<AssignedTicket?> ClaimTicket(string userId, string ticketId, CancellationToken ct)
    {
        if (ConnectionString is null) return null;
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            await using var ticket = new MySqlCommand("SELECT t.client_id,t.site_id,t.assigned_user_id,t.status,EXISTS(SELECT 1 FROM user_roles WHERE user_id=@id AND role_code='ADMIN'),EXISTS(SELECT 1 FROM user_roles WHERE user_id=@id AND role_code IN ('TECHNICIAN','TEAM_LEAD','ADMIN')) FROM tickets t WHERE t.id=@ticket AND (EXISTS(SELECT 1 FROM user_roles WHERE user_id=@id AND role_code='ADMIN') OR EXISTS(SELECT 1 FROM client_users cu WHERE cu.user_id=@id AND cu.client_id=t.client_id)) FOR UPDATE", connection, transaction);
            ticket.Parameters.AddWithValue("@id", userId); ticket.Parameters.AddWithValue("@ticket", ticketId);
            await using var reader = await ticket.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            var clientId = reader.GetString(0); var siteId = reader.IsDBNull(1) ? null : reader.GetString(1); var assignedId = reader.IsDBNull(2) ? null : reader.GetString(2); var status = reader.GetString(3); var isAdmin = reader.GetBoolean(4); var canClaim = reader.GetBoolean(5);
            await reader.DisposeAsync();
            if (!canClaim) throw new UnauthorizedAccessException();
            if (assignedId is not null) throw new InvalidOperationException("Ticket is already assigned.");
            await using var target = new MySqlCommand("SELECT 1 FROM users u JOIN user_roles r ON r.user_id=u.id WHERE u.id=@id AND u.active=1 AND (u.access_expires_at IS NULL OR u.access_expires_at>UTC_TIMESTAMP(6)) AND r.role_code IN ('TECHNICIAN','TEAM_LEAD','ADMIN') AND (r.role_code='ADMIN' OR EXISTS(SELECT 1 FROM client_users cu WHERE cu.user_id=u.id AND cu.client_id=@client)) AND (@site IS NULL OR r.role_code='ADMIN' OR NOT EXISTS(SELECT 1 FROM user_sites us WHERE us.user_id=u.id) OR EXISTS(SELECT 1 FROM user_sites us WHERE us.user_id=u.id AND us.site_id=@site))", connection, transaction);
            target.Parameters.AddWithValue("@id", userId); target.Parameters.AddWithValue("@client", clientId); target.Parameters.AddWithValue("@site", (object?)siteId ?? DBNull.Value);
            if (await target.ExecuteScalarAsync(ct) is null) throw new UnauthorizedAccessException();
            var nextStatus = status == "NEW" ? "OPEN" : status;
            await using (var update = new MySqlCommand("UPDATE tickets SET assigned_user_id=@actor,status=@status,updated_at=UTC_TIMESTAMP(6) WHERE id=@ticket", connection, transaction)) { update.Parameters.AddWithValue("@actor", userId); update.Parameters.AddWithValue("@status", nextStatus); update.Parameters.AddWithValue("@ticket", ticketId); await update.ExecuteNonQueryAsync(ct); }
            await using (var assignment = new MySqlCommand("INSERT INTO ticket_assignments(ticket_id,assigned_user_id,actor_user_id,reason) VALUES(@ticket,@actor,@actor,'Claimed by technician')", connection, transaction)) { assignment.Parameters.AddWithValue("@ticket", ticketId); assignment.Parameters.AddWithValue("@actor", userId); await assignment.ExecuteNonQueryAsync(ct); }
            await using (var history = new MySqlCommand("INSERT INTO ticket_history(ticket_id,actor_user_id,action,previous_value,new_value,reason) VALUES(@ticket,@actor,'TICKET.ASSIGNED',CAST('null' AS JSON),CAST(@value AS JSON),'Claimed by technician')", connection, transaction)) { history.Parameters.AddWithValue("@ticket", ticketId); history.Parameters.AddWithValue("@actor", userId); history.Parameters.AddWithValue("@value", System.Text.Json.JsonSerializer.Serialize(userId)); await history.ExecuteNonQueryAsync(ct); }
            var details = System.Text.Json.JsonSerializer.Serialize(new { assignedUserId = userId, reason = "Claimed by technician" });
            await using (var audit = new MySqlCommand("INSERT INTO audit_logs(actor_user_id,action,entity_type,entity_id,details) VALUES(@actor,'TICKET.ASSIGNED','TICKET',@ticket,CAST(@details AS JSON))", connection, transaction)) { audit.Parameters.AddWithValue("@actor", userId); audit.Parameters.AddWithValue("@ticket", ticketId); audit.Parameters.AddWithValue("@details", details); await audit.ExecuteNonQueryAsync(ct); }
            await QueueTicketEvent(connection,transaction,userId,ticketId,null,"Ticket claimed",ct);
            await transaction.CommitAsync(ct);
            return new AssignedTicket(ticketId, userId);
        }
        catch { await transaction.RollbackAsync(ct); throw; }
    }

    public async Task<CreatedApproval?> RequestTicketApproval(string userId, string ticketId, ApprovalRequest body, CancellationToken ct)
    {
        if (ConnectionString is null) return null;
        if (await GetTicketDetail(userId, ticketId, ct) is null) return null;
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            await using var grants = new MySqlCommand("SELECT EXISTS(SELECT 1 FROM user_roles WHERE user_id=@id AND role_code='INTERN'),EXISTS(SELECT 1 FROM user_roles WHERE user_id=@id AND role_code IN ('TECHNICIAN','INTERN','TEAM_LEAD','MANAGER','ADMIN'))", connection, transaction);
            grants.Parameters.AddWithValue("@id", userId);
            await using var reader = await grants.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct) || !reader.GetBoolean(1)) throw new UnauthorizedAccessException();
            var isIntern = reader.GetBoolean(0);
            await reader.DisposeAsync();
            if (isIntern) throw new UnauthorizedAccessException();
            var approvalId = Guid.NewGuid().ToString("D");
            await using (var insert = new MySqlCommand("INSERT INTO ticket_approvals(id,ticket_id,requested_by_user_id,reason) VALUES(@approval,@ticket,@actor,@reason)", connection, transaction))
            {
                insert.Parameters.AddWithValue("@approval", approvalId); insert.Parameters.AddWithValue("@ticket", ticketId); insert.Parameters.AddWithValue("@actor", userId); insert.Parameters.AddWithValue("@reason", body.Reason.Trim());
                try { await insert.ExecuteNonQueryAsync(ct); }
                catch (MySqlException exception) when (exception.Number == 1062) { throw new InvalidOperationException("An approval is already pending for this ticket."); }
            }
            await using (var update = new MySqlCommand("UPDATE tickets SET approval_status='PENDING',updated_at=UTC_TIMESTAMP(6) WHERE id=@ticket", connection, transaction)) { update.Parameters.AddWithValue("@ticket", ticketId); await update.ExecuteNonQueryAsync(ct); }
            var details = System.Text.Json.JsonSerializer.Serialize(new { approvalId });
            await using (var history = new MySqlCommand("INSERT INTO ticket_history(ticket_id,actor_user_id,action,new_value,reason) VALUES(@ticket,@actor,'TICKET.APPROVAL_REQUESTED',CAST(@value AS JSON),@reason)", connection, transaction)) { history.Parameters.AddWithValue("@ticket", ticketId); history.Parameters.AddWithValue("@actor", userId); history.Parameters.AddWithValue("@value", details); history.Parameters.AddWithValue("@reason", body.Reason.Trim()); await history.ExecuteNonQueryAsync(ct); }
            await using (var audit = new MySqlCommand("INSERT INTO audit_logs(actor_user_id,action,entity_type,entity_id,details) VALUES(@actor,'TICKET.APPROVAL_REQUESTED','TICKET',@ticket,CAST(@details AS JSON))", connection, transaction)) { audit.Parameters.AddWithValue("@actor", userId); audit.Parameters.AddWithValue("@ticket", ticketId); audit.Parameters.AddWithValue("@details", details); await audit.ExecuteNonQueryAsync(ct); }
            await transaction.CommitAsync(ct);
            return new CreatedApproval(approvalId);
        }
        catch { await transaction.RollbackAsync(ct); throw; }
    }

    public async Task<CreatedTicketAttachment?> AddTicketAttachment(string userId, string ticketId, CreateAttachment body, CancellationToken ct)
    {
        if (ConnectionString is null) return null;
        if (await GetTicketDetail(userId, ticketId, ct) is null) return null;
        string[] allowedTypes = ["application/pdf", "application/zip", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "image/gif", "image/jpeg", "image/png", "text/csv", "text/plain"];
        if (string.IsNullOrWhiteSpace(body.FileName) || body.FileName.Length > 255 || body.FileName.Contains('/') || body.FileName.Contains('\\') || body.FileName.Contains('\0') || !allowedTypes.Contains(body.ContentType, StringComparer.Ordinal) || body.Visibility is not (null or "PUBLIC" or "INTERNAL")) throw new ArgumentException("The attachment name, type, or visibility is invalid.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(body.ContentBase64); }
        catch (FormatException) { throw new ArgumentException("The attachment data is invalid."); }
        if (bytes.Length is < 1 or > 5 * 1024 * 1024) throw new ArgumentException("Attachments must be between 1 byte and 5 MB.");
        var attachmentId = Guid.NewGuid().ToString("D");
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var storageKey = $"mysql/{ticketId}/{attachmentId}";
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            await using var insert = new MySqlCommand("INSERT INTO ticket_attachments(id,ticket_id,uploaded_by_user_id,file_name,content_type,size_bytes,storage_key,sha256,content,visibility) VALUES(@id,@ticket,@actor,@name,@type,@size,@key,@hash,@content,@visibility)", connection, transaction);
            insert.Parameters.AddWithValue("@id", attachmentId); insert.Parameters.AddWithValue("@ticket", ticketId); insert.Parameters.AddWithValue("@actor", userId); insert.Parameters.AddWithValue("@name", body.FileName.Trim()); insert.Parameters.AddWithValue("@type", body.ContentType); insert.Parameters.AddWithValue("@size", bytes.Length); insert.Parameters.AddWithValue("@key", storageKey); insert.Parameters.AddWithValue("@hash", hash); insert.Parameters.Add("@content", MySqlDbType.LongBlob).Value = bytes; insert.Parameters.AddWithValue("@visibility", body.Visibility ?? "INTERNAL"); await insert.ExecuteNonQueryAsync(ct);
            var metadata = System.Text.Json.JsonSerializer.Serialize(new { attachmentId, fileName = body.FileName.Trim(), sizeBytes = bytes.Length, sha256 = hash });
            await using (var history = new MySqlCommand("INSERT INTO ticket_history(ticket_id,actor_user_id,action,new_value) VALUES(@ticket,@actor,'TICKET.ATTACHMENT_ADDED',CAST(@value AS JSON))", connection, transaction)) { history.Parameters.AddWithValue("@ticket", ticketId); history.Parameters.AddWithValue("@actor", userId); history.Parameters.AddWithValue("@value", metadata); await history.ExecuteNonQueryAsync(ct); }
            await using (var audit = new MySqlCommand("INSERT INTO audit_logs(actor_user_id,action,entity_type,entity_id,details) VALUES(@actor,'TICKET.ATTACHMENT_ADDED','TICKET',@ticket,CAST(@details AS JSON))", connection, transaction)) { audit.Parameters.AddWithValue("@actor", userId); audit.Parameters.AddWithValue("@ticket", ticketId); audit.Parameters.AddWithValue("@details", metadata); await audit.ExecuteNonQueryAsync(ct); }
            await transaction.CommitAsync(ct);
            return new CreatedTicketAttachment(attachmentId);
        }
        catch { await transaction.RollbackAsync(ct); throw; }
    }

    public async Task<TicketAttachmentFile?> GetTicketAttachment(string userId, string ticketId, string attachmentId, CancellationToken ct)
    {
        if (ConnectionString is null || await GetTicketDetail(userId, ticketId, ct) is null) return null;
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        await using var command = new MySqlCommand("SELECT file_name,content_type,content,storage_key,sha256 FROM ticket_attachments WHERE id=@id AND ticket_id=@ticket", connection);
        command.Parameters.AddWithValue("@id", attachmentId); command.Parameters.AddWithValue("@ticket", ticketId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var name = reader.GetString(0); var type = reader.GetString(1); var bytes = (byte[])reader.GetValue(2); var key = reader.GetString(3); var hash = reader.GetString(4);
        await reader.DisposeAsync();
        if (bytes.Length == 0)
        {
            if (Path.IsPathRooted(key) || key.Contains('\\') || key.Split('/').Any(part => part is "" or "." or "..")) throw new InvalidOperationException("The stored attachment path is invalid.");
            var root = Path.GetFullPath(configuration["ASTRA_PRIVATE_STORAGE_ROOT"] ?? Path.Combine(Directory.GetCurrentDirectory(), "storage"));
            var filePath = Path.GetFullPath(Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar)));
            if (!filePath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidOperationException("The stored attachment path is invalid.");
            if (!File.Exists(filePath)) return null;
            bytes = await File.ReadAllBytesAsync(filePath, ct);
        }
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(hash))) throw new InvalidOperationException("The stored attachment failed its integrity check.");
        return new TicketAttachmentFile(name, type, bytes);
    }

    public async Task<ApprovalDecisionResult?> DecideTicketApproval(string userId, string ticketId, ApprovalDecision body, CancellationToken ct)
    {
        if (ConnectionString is null) return null;
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            const string permissionSql = "SELECT EXISTS(SELECT 1 FROM user_roles ur LEFT JOIN role_access_policies rp ON rp.role_code=ur.role_code WHERE ur.user_id=@id AND ur.role_code IN ('TECHNICIAN','TEAM_LEAD','MANAGER','ADMIN') AND (rp.role_code IS NULL OR JSON_CONTAINS(rp.permissions,JSON_QUOTE('TICKETS_APPROVE')))),EXISTS(SELECT 1 FROM user_permissions WHERE user_id=@id AND permission_code='TICKETS_APPROVE'),EXISTS(SELECT 1 FROM user_access_policies WHERE user_id=@id AND JSON_CONTAINS(allow_permissions,JSON_QUOTE('TICKETS_APPROVE'))),EXISTS(SELECT 1 FROM user_access_policies WHERE user_id=@id AND JSON_CONTAINS(deny_permissions,JSON_QUOTE('TICKETS_APPROVE'))),EXISTS(SELECT 1 FROM staff_group_members gm JOIN access_groups g ON g.id=gm.group_id WHERE gm.user_id=@id AND g.active=1 AND g.kind='STAFF' AND JSON_CONTAINS(g.permissions,JSON_QUOTE('TICKETS_APPROVE')))";
            await using (var permission = new MySqlCommand(permissionSql, connection, transaction))
            {
                permission.Parameters.AddWithValue("@id", userId);
                await using var reader = await permission.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct) || !(reader.GetBoolean(0) || reader.GetBoolean(1) || reader.GetBoolean(2) || reader.GetBoolean(4)) || reader.GetBoolean(3)) throw new UnauthorizedAccessException();
            }
            await using var pending = new MySqlCommand("SELECT id FROM ticket_approvals WHERE ticket_id=@ticket AND status='PENDING' FOR UPDATE", connection, transaction);
            pending.Parameters.AddWithValue("@ticket", ticketId);
            var approvalId = await pending.ExecuteScalarAsync(ct) as string;
            if (approvalId is null) throw new InvalidOperationException("No pending approval exists for this ticket.");
            await using (var update = new MySqlCommand("UPDATE ticket_approvals SET status=@status,decision_note=@note,decided_by_user_id=@actor,decided_at=UTC_TIMESTAMP(6) WHERE id=@approval", connection, transaction)) { update.Parameters.AddWithValue("@status", body.Decision); update.Parameters.AddWithValue("@note", body.Note.Trim()); update.Parameters.AddWithValue("@actor", userId); update.Parameters.AddWithValue("@approval", approvalId); await update.ExecuteNonQueryAsync(ct); }
            await using (var ticket = new MySqlCommand("UPDATE tickets SET approval_status=@status,updated_at=UTC_TIMESTAMP(6) WHERE id=@ticket", connection, transaction)) { ticket.Parameters.AddWithValue("@status", body.Decision); ticket.Parameters.AddWithValue("@ticket", ticketId); if (await ticket.ExecuteNonQueryAsync(ct) == 0) return null; }
            var action = "TICKET.APPROVAL_" + body.Decision;
            var detail = System.Text.Json.JsonSerializer.Serialize(new { note = body.Note.Trim() });
            await using (var history = new MySqlCommand("INSERT INTO ticket_history(ticket_id,actor_user_id,action,previous_value,new_value,reason) VALUES(@ticket,@actor,@action,CAST('\\\"PENDING\\\"' AS JSON),CAST(@next AS JSON),@reason)", connection, transaction)) { history.Parameters.AddWithValue("@ticket", ticketId); history.Parameters.AddWithValue("@actor", userId); history.Parameters.AddWithValue("@action", action); history.Parameters.AddWithValue("@next", System.Text.Json.JsonSerializer.Serialize(body.Decision)); history.Parameters.AddWithValue("@reason", body.Note.Trim()); await history.ExecuteNonQueryAsync(ct); }
            await using (var audit = new MySqlCommand("INSERT INTO audit_logs(actor_user_id,action,entity_type,entity_id,details) VALUES(@actor,@action,'TICKET',@ticket,CAST(@details AS JSON))", connection, transaction)) { audit.Parameters.AddWithValue("@actor", userId); audit.Parameters.AddWithValue("@action", action); audit.Parameters.AddWithValue("@ticket", ticketId); audit.Parameters.AddWithValue("@details", detail); await audit.ExecuteNonQueryAsync(ct); }
            await transaction.CommitAsync(ct);
            return new ApprovalDecisionResult(ticketId, body.Decision);
        }
        catch { await transaction.RollbackAsync(ct); throw; }
    }

    public async Task<UpdatedTicket?> UpdateTicket(string userId, string ticketId, UpdateTicketRequest body, CancellationToken ct)
    {
        if (ConnectionString is null) return null;
        if (body.Status is null && body.Priority is null && body.CategoryId is null && body.TagIds is null) throw new ArgumentException("Select at least one ticket change.");
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            await using var ticket = new MySqlCommand("SELECT t.status,t.priority,t.category_id,t.assigned_user_id,t.client_id,t.created_at,EXISTS(SELECT 1 FROM user_roles WHERE user_id=@id AND role_code='ADMIN'),EXISTS(SELECT 1 FROM user_roles WHERE user_id=@id AND role_code IN ('MANAGER','TEAM_LEAD')),EXISTS(SELECT 1 FROM user_roles WHERE user_id=@id AND role_code='INTERN'),EXISTS(SELECT 1 FROM user_roles WHERE user_id=@id AND role_code IN ('TECHNICIAN','INTERN','TEAM_LEAD','MANAGER','ADMIN')) FROM tickets t WHERE t.id=@ticket AND (EXISTS(SELECT 1 FROM user_roles WHERE user_id=@id AND role_code='ADMIN') OR (EXISTS(SELECT 1 FROM client_users cu WHERE cu.user_id=@id AND cu.client_id=t.client_id) AND (NOT EXISTS(SELECT 1 FROM user_sites us WHERE us.user_id=@id) OR EXISTS(SELECT 1 FROM user_sites us WHERE us.user_id=@id AND us.site_id=t.site_id)) AND (EXISTS(SELECT 1 FROM user_roles WHERE user_id=@id AND role_code IN ('MANAGER','TEAM_LEAD')) OR t.assigned_user_id=@id))) FOR UPDATE", connection, transaction);
            ticket.Parameters.AddWithValue("@id", userId); ticket.Parameters.AddWithValue("@ticket", ticketId);
            await using var reader = await ticket.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            var oldStatus = reader.GetString(0); var oldPriority = reader.GetString(1); var oldCategory = reader.GetString(2); var oldAssignee = reader.IsDBNull(3) ? null : reader.GetString(3); var clientId = reader.GetString(4); var createdAt = reader.GetDateTime(5);
            var isAdmin = reader.GetBoolean(6); var isWide = reader.GetBoolean(7) || isAdmin; var isIntern = reader.GetBoolean(8); var isStaff = reader.GetBoolean(9);
            await reader.DisposeAsync();
            if (!isStaff || isIntern) throw new UnauthorizedAccessException();
            if (!isAdmin && oldAssignee != userId && !isWide) throw new UnauthorizedAccessException();
            if (body.CategoryId is not null)
            {
                await using var category = new MySqlCommand("SELECT 1 FROM categories WHERE id=@id AND active", connection, transaction); category.Parameters.AddWithValue("@id", body.CategoryId);
                if (await category.ExecuteScalarAsync(ct) is null) throw new ArgumentException("The selected category is unavailable.");
            }
            var tags = body.TagIds?.Distinct(StringComparer.Ordinal).ToArray();
            if (tags is not null) foreach (var tag in tags) { await using var check = new MySqlCommand("SELECT 1 FROM tags WHERE id=@id AND active", connection, transaction); check.Parameters.AddWithValue("@id", tag); if (await check.ExecuteScalarAsync(ct) is null) throw new ArgumentException("A selected tag is unavailable."); }
            await using (var update = new MySqlCommand("UPDATE tickets SET status=COALESCE(@status,status),priority=COALESCE(@priority,priority),category_id=COALESCE(@category,category_id),resolved_at=CASE WHEN @status='RESOLVED' THEN COALESCE(resolved_at,UTC_TIMESTAMP(6)) WHEN @status IS NOT NULL AND @status<>'RESOLVED' THEN NULL ELSE resolved_at END,closed_at=CASE WHEN @status='CLOSED' THEN COALESCE(closed_at,UTC_TIMESTAMP(6)) WHEN @status IS NOT NULL AND @status<>'CLOSED' THEN NULL ELSE closed_at END,updated_at=UTC_TIMESTAMP(6) WHERE id=@ticket", connection, transaction))
            {
                update.Parameters.AddWithValue("@status", (object?)body.Status ?? DBNull.Value); update.Parameters.AddWithValue("@priority", (object?)body.Priority ?? DBNull.Value); update.Parameters.AddWithValue("@category", (object?)body.CategoryId ?? DBNull.Value); update.Parameters.AddWithValue("@ticket", ticketId); await update.ExecuteNonQueryAsync(ct);
            }
            if (body.Status is not null && body.Status != oldStatus)
            {
                await using var sla = new MySqlCommand("UPDATE ticket_sla SET resolution_completed_at=CASE WHEN @status IN ('RESOLVED','CLOSED') THEN COALESCE(resolution_completed_at,UTC_TIMESTAMP(6)) WHEN @status<>'CANCELLED' THEN NULL ELSE resolution_completed_at END,updated_at=UTC_TIMESTAMP(6) WHERE ticket_id=@ticket", connection, transaction); sla.Parameters.AddWithValue("@status", body.Status); sla.Parameters.AddWithValue("@ticket", ticketId); await sla.ExecuteNonQueryAsync(ct);
            }
            var priorityChanged = body.Priority is not null && body.Priority != oldPriority;
            if (priorityChanged)
            {
                var calendar = await GetSlaCalendar(connection, transaction, body.Priority!, ct);
                if (calendar is not null)
                {
                    var start = DateTime.SpecifyKind(createdAt, DateTimeKind.Utc);
                    var responseWarning = SlaBusinessCalendar.Add(calendar, start, (int)Math.Ceiling(calendar.ResponseMinutes * calendar.WarningPercent / 100.0));
                    var responseDue = SlaBusinessCalendar.Add(calendar, start, calendar.ResponseMinutes);
                    var resolutionWarning = SlaBusinessCalendar.Add(calendar, start, (int)Math.Ceiling(calendar.ResolutionMinutes * calendar.WarningPercent / 100.0));
                    var resolutionDue = SlaBusinessCalendar.Add(calendar, start, calendar.ResolutionMinutes);
                    await using var updateSla = new MySqlCommand("UPDATE ticket_sla SET policy_id=@policy,calendar_id=@calendar,target_priority=@priority,response_warning_at=@rw,response_due_at=@rd,resolution_warning_at=@sw,resolution_due_at=@sd,paused_at=NULL,response_warning_remaining_minutes=NULL,response_remaining_minutes=NULL,resolution_warning_remaining_minutes=NULL,resolution_remaining_minutes=NULL,updated_at=UTC_TIMESTAMP(6) WHERE ticket_id=@ticket", connection, transaction);
                    updateSla.Parameters.AddWithValue("@policy", calendar.PolicyId); updateSla.Parameters.AddWithValue("@calendar", calendar.Id); updateSla.Parameters.AddWithValue("@priority", body.Priority); updateSla.Parameters.AddWithValue("@rw", responseWarning); updateSla.Parameters.AddWithValue("@rd", responseDue); updateSla.Parameters.AddWithValue("@sw", resolutionWarning); updateSla.Parameters.AddWithValue("@sd", resolutionDue); updateSla.Parameters.AddWithValue("@ticket", ticketId); await updateSla.ExecuteNonQueryAsync(ct);
                }
            }
            var oldPaused = oldStatus is "WAITING_FOR_CLIENT" or "WAITING_FOR_VENDOR" or "ON_HOLD";
            var nextStatus = body.Status ?? oldStatus;
            var now = DateTime.UtcNow;
            if ((nextStatus is "WAITING_FOR_CLIENT" or "WAITING_FOR_VENDOR" or "ON_HOLD") && (!oldPaused || priorityChanged))
            {
                await PauseTicketSla(connection, transaction, ticketId, now, ct);
            }
            else if (oldPaused && body.Status is not null)
            {
                await ResumeTicketSla(connection, transaction, ticketId, now, ct);
            }
            async Task History(string action, object? previous, object? next)
            {
                var oldJson = previous is null ? null : System.Text.Json.JsonSerializer.Serialize(previous);
                var newJson = next is null ? null : System.Text.Json.JsonSerializer.Serialize(next);
                await using var history = new MySqlCommand("INSERT INTO ticket_history(ticket_id,actor_user_id,action,previous_value,new_value,reason) VALUES(@ticket,@actor,@action,CAST(@previous AS JSON),CAST(@next AS JSON),@reason)", connection, transaction);
                history.Parameters.AddWithValue("@ticket", ticketId); history.Parameters.AddWithValue("@actor", userId); history.Parameters.AddWithValue("@action", action); history.Parameters.AddWithValue("@previous", (object?)oldJson ?? DBNull.Value); history.Parameters.AddWithValue("@next", (object?)newJson ?? DBNull.Value); history.Parameters.AddWithValue("@reason", (object?)(body.Reason?.Trim()) ?? DBNull.Value); await history.ExecuteNonQueryAsync(ct);
            }
            if (body.Status is not null && body.Status != oldStatus) await History("TICKET.STATUS_CHANGED", oldStatus, body.Status);
            if (body.Priority is not null && body.Priority != oldPriority) await History("TICKET.PRIORITY_CHANGED", oldPriority, body.Priority);
            if (body.CategoryId is not null && body.CategoryId != oldCategory) await History("TICKET.CATEGORY_CHANGED", oldCategory, body.CategoryId);
            if (tags is not null)
            {
                var oldTags = new List<string>();
                await using (var oldTagQuery = new MySqlCommand("SELECT tag_id FROM ticket_tags WHERE ticket_id=@ticket", connection, transaction))
                {
                    oldTagQuery.Parameters.AddWithValue("@ticket", ticketId);
                    await using var oldTagReader = await oldTagQuery.ExecuteReaderAsync(ct);
                    while (await oldTagReader.ReadAsync(ct)) oldTags.Add(oldTagReader.GetString(0));
                }
                await using (var remove = new MySqlCommand("DELETE FROM ticket_tags WHERE ticket_id=@ticket", connection, transaction)) { remove.Parameters.AddWithValue("@ticket", ticketId); await remove.ExecuteNonQueryAsync(ct); }
                foreach (var tag in tags) { await using var add = new MySqlCommand("INSERT INTO ticket_tags(ticket_id,tag_id,added_by_user_id) VALUES(@ticket,@tag,@actor)", connection, transaction); add.Parameters.AddWithValue("@ticket", ticketId); add.Parameters.AddWithValue("@tag", tag); add.Parameters.AddWithValue("@actor", userId); await add.ExecuteNonQueryAsync(ct); }
                await History("TICKET.TAGS_CHANGED", oldTags, tags);
            }
            var detail = System.Text.Json.JsonSerializer.Serialize(new { status = body.Status, priority = body.Priority, categoryId = body.CategoryId, tagIds = tags, reason = body.Reason });
            await using (var audit = new MySqlCommand("INSERT INTO audit_logs(actor_user_id,action,entity_type,entity_id,details) VALUES(@actor,'TICKET.UPDATED','TICKET',@ticket,CAST(@details AS JSON))", connection, transaction)) { audit.Parameters.AddWithValue("@actor", userId); audit.Parameters.AddWithValue("@ticket", ticketId); audit.Parameters.AddWithValue("@details", detail); await audit.ExecuteNonQueryAsync(ct); }
            await QueueTicketEvent(connection,transaction,userId,ticketId,null,"Ticket updated",ct);
            await transaction.CommitAsync(ct);
            return new UpdatedTicket(ticketId);
        }
        catch { await transaction.RollbackAsync(ct); throw; }
    }

    private static async Task<string> AllocateTicketNumber(MySqlConnection connection, MySqlTransaction transaction, string clientId, string? siteId, CancellationToken ct)
    {
        var table = siteId is null ? "client_ticket_sequences" : "site_ticket_sequences";
        var key = siteId is null ? "client_id" : "site_id";
        var entity = siteId is null ? "clients" : "sites";
        var activePredicate = siteId is null ? "status='ACTIVE'" : "active=1";
        var entityId = siteId ?? clientId;
        await using var prefixQuery = new MySqlCommand($"SELECT ticket_prefix FROM {entity} WHERE id=@id AND {activePredicate} FOR UPDATE", connection, transaction); prefixQuery.Parameters.AddWithValue("@id", entityId);
        var prefixValue = await prefixQuery.ExecuteScalarAsync(ct) as string ?? throw new ArgumentException("The selected client or site is inactive.");
        await using (var create = new MySqlCommand($"INSERT IGNORE INTO {table}({key},next_number) VALUES(@id,1)", connection, transaction)) { create.Parameters.AddWithValue("@id", entityId); await create.ExecuteNonQueryAsync(ct); }
        await using var get = new MySqlCommand($"SELECT next_number FROM {table} WHERE {key}=@id FOR UPDATE", connection, transaction); get.Parameters.AddWithValue("@id", entityId);
        var number = Convert.ToInt64(await get.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
        await using var advance = new MySqlCommand($"UPDATE {table} SET next_number=next_number+1 WHERE {key}=@id", connection, transaction); advance.Parameters.AddWithValue("@id", entityId); await advance.ExecuteNonQueryAsync(ct);
        return $"{prefixValue}-{number.ToString(number < 1_000_000 ? "D6" : "0", System.Globalization.CultureInfo.InvariantCulture)}";
    }

    private static async Task<SlaCalendar?> GetSlaCalendar(MySqlConnection connection, MySqlTransaction transaction, string priority, CancellationToken ct)
    {
        await using var policy = new MySqlCommand("SELECT p.id,p.calendar_id,p.response_minutes,p.resolution_minutes,p.warning_percent,c.timezone,c.working_days,c.work_start,c.work_end FROM sla_policies p JOIN sla_calendars c ON c.id=p.calendar_id WHERE p.priority=@priority AND p.active=1 AND c.active=1", connection, transaction);
        policy.Parameters.AddWithValue("@priority", priority);
        await using var reader = await policy.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var policyId = reader.GetString(0); var calendarId = reader.GetString(1); var response = reader.GetInt32(2); var resolution = reader.GetInt32(3); var warning = reader.GetInt32(4); var zoneName = reader.GetString(5);
        var daysJson = reader.GetString(6); var start = reader.GetTimeSpan(7); var end = reader.GetTimeSpan(8);
        await reader.DisposeAsync();
        var days = System.Text.Json.JsonSerializer.Deserialize<int[]>(daysJson) ?? [];
        if (days.Length == 0 || days.Any(day => day is < 1 or > 7)) throw new InvalidOperationException("The active SLA calendar is invalid.");
        var holidays = new HashSet<DateOnly>();
        await using (var query = new MySqlCommand("SELECT holiday_date FROM sla_holidays WHERE calendar_id=@id", connection, transaction))
        {
            query.Parameters.AddWithValue("@id", calendarId);
            await using var holidayReader = await query.ExecuteReaderAsync(ct);
            while (await holidayReader.ReadAsync(ct)) holidays.Add(DateOnly.FromDateTime(holidayReader.GetDateTime(0)));
        }
        return new SlaCalendar(policyId, calendarId, response, resolution, warning, TimeZoneInfo.FindSystemTimeZoneById(zoneName), days.ToHashSet(), start, end, holidays);
    }

    private static async Task PauseTicketSla(MySqlConnection connection, MySqlTransaction transaction, string ticketId, DateTime now, CancellationToken ct)
    {
        await using var query = new MySqlCommand("SELECT calendar_id,response_warning_at,response_due_at,resolution_warning_at,resolution_due_at,response_completed_at,resolution_completed_at,paused_at FROM ticket_sla WHERE ticket_id=@ticket FOR UPDATE", connection, transaction);
        query.Parameters.AddWithValue("@ticket", ticketId);
        await using var reader = await query.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || !reader.IsDBNull(7)) return;
        var calendarId = reader.GetString(0);
        var rw = reader.GetDateTime(1); var rd = reader.GetDateTime(2); var sw = reader.GetDateTime(3); var sd = reader.GetDateTime(4);
        var responseComplete = !reader.IsDBNull(5); var resolutionComplete = !reader.IsDBNull(6);
        await reader.DisposeAsync();
        var calendar = await GetSlaCalendarById(connection, transaction, calendarId, ct);
        if (calendar is null) return;
        int? rwRemain = responseComplete ? null : SlaBusinessCalendar.Between(calendar, now, DateTime.SpecifyKind(rw, DateTimeKind.Utc));
        int? rdRemain = responseComplete ? null : SlaBusinessCalendar.Between(calendar, now, DateTime.SpecifyKind(rd, DateTimeKind.Utc));
        int? swRemain = resolutionComplete ? null : SlaBusinessCalendar.Between(calendar, now, DateTime.SpecifyKind(sw, DateTimeKind.Utc));
        int? sdRemain = resolutionComplete ? null : SlaBusinessCalendar.Between(calendar, now, DateTime.SpecifyKind(sd, DateTimeKind.Utc));
        await using var update = new MySqlCommand("UPDATE ticket_sla SET paused_at=@now,response_warning_remaining_minutes=@rw,response_remaining_minutes=@rd,resolution_warning_remaining_minutes=@sw,resolution_remaining_minutes=@sd,updated_at=@now WHERE ticket_id=@ticket", connection, transaction);
        update.Parameters.AddWithValue("@now", now); update.Parameters.AddWithValue("@rw", (object?)rwRemain ?? DBNull.Value); update.Parameters.AddWithValue("@rd", (object?)rdRemain ?? DBNull.Value); update.Parameters.AddWithValue("@sw", (object?)swRemain ?? DBNull.Value); update.Parameters.AddWithValue("@sd", (object?)sdRemain ?? DBNull.Value); update.Parameters.AddWithValue("@ticket", ticketId); await update.ExecuteNonQueryAsync(ct);
    }

    private static async Task ResumeTicketSla(MySqlConnection connection, MySqlTransaction transaction, string ticketId, DateTime now, CancellationToken ct)
    {
        await using var query = new MySqlCommand("SELECT calendar_id,response_warning_remaining_minutes,response_remaining_minutes,resolution_warning_remaining_minutes,resolution_remaining_minutes,paused_at FROM ticket_sla WHERE ticket_id=@ticket FOR UPDATE", connection, transaction);
        query.Parameters.AddWithValue("@ticket", ticketId);
        await using var reader = await query.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || reader.IsDBNull(5)) return;
        var calendarId = reader.GetString(0);
        int? rw = reader.IsDBNull(1) ? null : reader.GetInt32(1); int? rd = reader.IsDBNull(2) ? null : reader.GetInt32(2); int? sw = reader.IsDBNull(3) ? null : reader.GetInt32(3); int? sd = reader.IsDBNull(4) ? null : reader.GetInt32(4);
        await reader.DisposeAsync();
        var calendar = await GetSlaCalendarById(connection, transaction, calendarId, ct);
        if (calendar is null) return;
        await using var update = new MySqlCommand("UPDATE ticket_sla SET response_warning_at=COALESCE(@rw,response_warning_at),response_due_at=COALESCE(@rd,response_due_at),resolution_warning_at=COALESCE(@sw,resolution_warning_at),resolution_due_at=COALESCE(@sd,resolution_due_at),paused_at=NULL,response_warning_remaining_minutes=NULL,response_remaining_minutes=NULL,resolution_warning_remaining_minutes=NULL,resolution_remaining_minutes=NULL,updated_at=@now WHERE ticket_id=@ticket", connection, transaction);
        update.Parameters.AddWithValue("@rw", rw is null ? DBNull.Value : SlaBusinessCalendar.Add(calendar, now, rw.Value)); update.Parameters.AddWithValue("@rd", rd is null ? DBNull.Value : SlaBusinessCalendar.Add(calendar, now, rd.Value)); update.Parameters.AddWithValue("@sw", sw is null ? DBNull.Value : SlaBusinessCalendar.Add(calendar, now, sw.Value)); update.Parameters.AddWithValue("@sd", sd is null ? DBNull.Value : SlaBusinessCalendar.Add(calendar, now, sd.Value)); update.Parameters.AddWithValue("@now", now); update.Parameters.AddWithValue("@ticket", ticketId); await update.ExecuteNonQueryAsync(ct);
    }

    private static async Task<SlaCalendar?> GetSlaCalendarById(MySqlConnection connection, MySqlTransaction? transaction, string calendarId, CancellationToken ct)
    {
        await using var query = new MySqlCommand("SELECT timezone,working_days,work_start,work_end FROM sla_calendars WHERE id=@id", connection, transaction);
        query.Parameters.AddWithValue("@id", calendarId);
        await using var reader = await query.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var zoneName = reader.GetString(0); var daysJson = reader.GetString(1); var start = reader.GetTimeSpan(2); var end = reader.GetTimeSpan(3);
        await reader.DisposeAsync();
        var days = System.Text.Json.JsonSerializer.Deserialize<int[]>(daysJson) ?? [];
        if (days.Length == 0 || days.Any(day => day is < 1 or > 7)) throw new InvalidOperationException("The active SLA calendar is invalid.");
        var holidays = new HashSet<DateOnly>();
        await using (var holidayQuery = new MySqlCommand("SELECT holiday_date FROM sla_holidays WHERE calendar_id=@id", connection, transaction))
        {
            holidayQuery.Parameters.AddWithValue("@id", calendarId);
            await using var holidayReader = await holidayQuery.ExecuteReaderAsync(ct);
            while (await holidayReader.ReadAsync(ct)) holidays.Add(DateOnly.FromDateTime(holidayReader.GetDateTime(0)));
        }
        return new SlaCalendar("", calendarId, 0, 0, 0, TimeZoneInfo.FindSystemTimeZoneById(zoneName), days.ToHashSet(), start, end, holidays);
    }

    public async Task<TicketPage> ListTickets(string userId, string scope, string? status, string search, int page, int pageSize, CancellationToken ct, string? ticketId = null, IQueryCollection? filters = null)
    {
        if (ConnectionString is null) return new TicketPage(0, [], true);
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        // Determine authorization from the migrated account and relational grants on every request.
        const string grantSql = """
            SELECT EXISTS(SELECT 1 FROM user_roles WHERE user_id=@id AND role_code='ADMIN'),
                   EXISTS(SELECT 1 FROM user_roles WHERE user_id=@id AND role_code IN ('MANAGER','TEAM_LEAD')),
                   EXISTS(SELECT 1 FROM user_roles WHERE user_id=@id AND role_code IN ('TECHNICIAN','INTERN','MANAGER','TEAM_LEAD','ADMIN')),
                   EXISTS(SELECT 1 FROM user_permissions WHERE user_id=@id AND permission_code='TICKETS_TEAM_VIEW'),
                   (SELECT work_scope FROM users WHERE id=@id AND active=1 AND (access_expires_at IS NULL OR access_expires_at>UTC_TIMESTAMP(6)))
            """;
        await using var grantCommand = new MySqlCommand(grantSql, connection);
        grantCommand.Parameters.AddWithValue("@id", userId);
        await using var grants = await grantCommand.ExecuteReaderAsync(ct);
        if (!await grants.ReadAsync(ct)) return new TicketPage(0, [], true);
        var isAdmin = grants.GetBoolean(0);
        var isWide = grants.GetBoolean(1) || isAdmin;
        var isStaff = grants.GetBoolean(2);
        var canTeam = grants.GetBoolean(3) || isWide;
        var workScope = grants.IsDBNull(4) ? "CLIENT" : grants.GetString(4);
        await grants.DisposeAsync();
        if (!isStaff) return new TicketPage(0, [], true);
        if (!canTeam && isStaff)
            canTeam = await HasDefaultTeamPermission(connection, userId, ct);
        if ((scope is "all" or "unassigned") && !isWide || scope == "team" && !canTeam || scope == "internal" && workScope is not ("INTERNAL" or "BOTH" or "ALL"))
            return new TicketPage(0, [], true);

        var scopePredicate = scope switch
        {
            "all" => "1=1",
            "unassigned" => "t.assigned_user_id IS NULL",
            "team" => "(NOT EXISTS(SELECT 1 FROM user_sites us WHERE us.user_id=@id) OR EXISTS(SELECT 1 FROM user_sites us WHERE us.user_id=@id AND us.site_id=t.site_id))",
            "internal" => "(t.client_id='00000000-0000-4000-8000-000000000132' AND (NOT EXISTS(SELECT 1 FROM department_users du WHERE du.user_id=@id) OR EXISTS(SELECT 1 FROM department_users du WHERE du.user_id=@id AND du.department_id=t.department_id)))",
            _ => "(t.assigned_user_id=@id OR t.created_by_user_id=@id OR EXISTS(SELECT 1 FROM ticket_collaborators tc WHERE tc.ticket_id=t.id AND tc.user_id=@id))"
        };
        var accessPredicate = isAdmin ? "1=1" : "((t.client_id IN (SELECT client_id FROM client_users WHERE user_id=@id) OR t.site_id IN (SELECT site_id FROM user_sites WHERE user_id=@id)) AND (NOT EXISTS(SELECT 1 FROM user_sites WHERE user_id=@id) OR t.site_id IN (SELECT site_id FROM user_sites WHERE user_id=@id)))";
        var predicate = $"({accessPredicate}) AND ({scopePredicate}) AND (@ticketId IS NULL OR t.id=@ticketId) AND (@status IS NULL OR t.status=@status) AND (@search='' OR t.ticket_number LIKE @pattern OR t.subject LIKE @pattern OR t.description LIKE @pattern OR c.name LIKE @pattern OR r.name LIKE @pattern)";
        var extraFilters = TicketExtraFilters(filters);
        predicate += extraFilters.Sql;
        var from = " FROM tickets t JOIN clients c ON c.id=t.client_id JOIN contacts r ON r.id=t.requester_id LEFT JOIN sites s ON s.id=t.site_id LEFT JOIN categories cat ON cat.id=t.category_id LEFT JOIN categories parentcat ON parentcat.id=cat.parent_id LEFT JOIN users u ON u.id=t.assigned_user_id LEFT JOIN ticket_sla sla ON sla.ticket_id=t.id ";
        await using var countCommand = new MySqlCommand("SELECT COUNT(*)" + from + " WHERE " + predicate, connection);
        AddTicketParameters(countCommand, userId, status, search, pageSize, page, ticketId);
        foreach (var (name,value) in extraFilters.Args) countCommand.Parameters.AddWithValue(name,value ?? DBNull.Value);
        var total = Convert.ToInt32(await countCommand.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
        await using var command = new MySqlCommand("SELECT t.id,t.ticket_number,t.client_id,t.requester_id,t.site_id,t.category_id,t.assigned_user_id,t.created_by_user_id,t.subject,t.description,t.source,t.status,t.priority,t.approval_status,t.created_at,t.updated_at,c.name AS client_name,r.name AS requester_name,r.email AS requester_email,s.name AS site_name,CASE WHEN parentcat.name IS NULL THEN cat.name ELSE CONCAT(parentcat.name,' → ',cat.name) END AS category_name,u.display_name AS assigned_name,CASE WHEN sla.resolution_due_at IS NULL THEN 'NOT_TRACKED' WHEN t.status='CANCELLED' THEN 'NOT_TRACKED' WHEN sla.resolution_completed_at IS NOT NULL AND sla.resolution_completed_at<=sla.resolution_due_at THEN 'MET' WHEN sla.paused_at IS NOT NULL THEN 'PAUSED' WHEN UTC_TIMESTAMP(6)>=sla.resolution_due_at THEN 'BREACHED' WHEN UTC_TIMESTAMP(6)>=sla.resolution_warning_at THEN 'AT_RISK' ELSE 'ON_TRACK' END AS sla_status,COALESCE((SELECT JSON_ARRAYAGG(JSON_OBJECT('id',tg.id,'name',tg.name)) FROM ticket_tags tt JOIN tags tg ON tg.id=tt.tag_id WHERE tt.ticket_id=t.id),JSON_ARRAY()) AS tags" + from + " WHERE " + predicate + " ORDER BY t.updated_at DESC LIMIT @take OFFSET @skip", connection);
        AddTicketParameters(command, userId, status, search, pageSize, page, ticketId);
        foreach (var (name,value) in extraFilters.Args) command.Parameters.AddWithValue(name,value ?? DBNull.Value);
        var items = new List<Dictionary<string, object?>>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var item = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++) item[reader.GetName(i)] = ReadValue(reader, i);
            items.Add(item);
        }
        return new TicketPage(total, items, false);
    }

    public async Task<object?> GetTicketOptions(string userId, CancellationToken ct)
    {
        if (ConnectionString is null) return null;
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        await using var check = new MySqlCommand("SELECT EXISTS(SELECT 1 FROM user_roles WHERE user_id=@id AND role_code IN ('TECHNICIAN','INTERN','TEAM_LEAD','MANAGER','ADMIN'))", connection);
        check.Parameters.AddWithValue("@id", userId);
        if (Convert.ToInt32(await check.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture) != 1) return null;
        var clients = await ReadRows(connection, "SELECT c.id,c.name FROM clients c WHERE c.status='ACTIVE' AND (EXISTS(SELECT 1 FROM user_roles WHERE user_id=@id AND role_code='ADMIN') OR EXISTS(SELECT 1 FROM client_users cu WHERE cu.client_id=c.id AND cu.user_id=@id)) ORDER BY c.name", userId, ct);
        var contacts = await ReadRows(connection, "SELECT x.id,x.client_id,x.name,x.email FROM contacts x WHERE x.active AND (EXISTS(SELECT 1 FROM user_roles WHERE user_id=@id AND role_code='ADMIN') OR EXISTS(SELECT 1 FROM client_users cu WHERE cu.client_id=x.client_id AND cu.user_id=@id)) ORDER BY x.name", userId, ct);
        var sites = await ReadRows(connection, "SELECT s.id,s.client_id,s.name FROM sites s WHERE s.active AND (EXISTS(SELECT 1 FROM user_roles WHERE user_id=@id AND role_code='ADMIN') OR (EXISTS(SELECT 1 FROM client_users cu WHERE cu.client_id=s.client_id AND cu.user_id=@id) AND (NOT EXISTS(SELECT 1 FROM user_sites us WHERE us.user_id=@id) OR EXISTS(SELECT 1 FROM user_sites us WHERE us.user_id=@id AND us.site_id=s.id)))) ORDER BY s.name", userId, ct);
        var categories = await ReadRows(connection, "SELECT c.id,c.parent_id,CASE WHEN p.name IS NULL THEN c.name ELSE CONCAT(p.name,' → ',c.name) END AS name FROM categories c LEFT JOIN categories p ON p.id=c.parent_id WHERE c.active ORDER BY COALESCE(p.name,c.name),p.name,c.name", userId, ct);
        var tags = await ReadRows(connection, "SELECT id,name FROM tags WHERE active ORDER BY name", userId, ct);
        var technicians = await ReadRows(connection, "SELECT DISTINCT u.id,u.display_name AS name,u.email FROM users u JOIN user_roles r ON r.user_id=u.id WHERE u.active AND r.role_code IN ('INTERN','TECHNICIAN','TEAM_LEAD','MANAGER','ADMIN') AND (EXISTS(SELECT 1 FROM user_roles WHERE user_id=@id AND role_code='ADMIN') OR EXISTS(SELECT 1 FROM client_users cu JOIN client_users mine ON mine.client_id=cu.client_id WHERE cu.user_id=u.id AND mine.user_id=@id)) ORDER BY u.display_name", userId, ct);
        var sources = await ReadRows(connection, "SELECT code,name FROM ticket_sources WHERE active ORDER BY sort_order,name", userId, ct);
        var departments = await ReadRows(connection, "SELECT id,name FROM departments WHERE active ORDER BY name", userId, ct);
        return new { clients, contacts, sites, categories, tags, technicians, sources, departments,
            statuses = new[] { "NEW", "OPEN", "IN_PROGRESS", "WAITING_FOR_CLIENT", "WAITING_FOR_VENDOR", "ON_HOLD", "RESOLVED", "CLOSED", "CANCELLED" },
            priorities = new[] { "P1_CRITICAL", "P2_HIGH", "P3_MEDIUM", "P4_LOW" } };
    }

    public async Task<object?> GetTicketDetail(string userId, string ticketId, CancellationToken ct)
    {
        if (ConnectionString is null) return null;
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        await using var grants = new MySqlCommand("SELECT EXISTS(SELECT 1 FROM user_roles WHERE user_id=@id AND role_code IN ('MANAGER','TEAM_LEAD','ADMIN')),EXISTS(SELECT 1 FROM user_roles WHERE user_id=@id AND role_code='ADMIN'),EXISTS(SELECT 1 FROM user_permissions WHERE user_id=@id AND permission_code='TICKETS_TEAM_VIEW'),work_scope FROM users WHERE id=@id AND active=1 AND (access_expires_at IS NULL OR access_expires_at>UTC_TIMESTAMP(6))", connection);
        grants.Parameters.AddWithValue("@id", userId);
        await using var reader = await grants.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var isWide = reader.GetBoolean(0);
        var workScope = reader.GetString(3);
        var canTeam = reader.GetBoolean(2);
        await reader.DisposeAsync();
        if (!canTeam && isWide) canTeam = true;
        if (!canTeam) canTeam = await HasDefaultTeamPermission(connection, userId, ct);
        var scope = isWide ? "all" : canTeam ? "team" : workScope is "INTERNAL" or "BOTH" or "ALL" ? "internal" : "my";
        var page = await ListTickets(userId, scope, null, "", 1, 1, ct, ticketId);
        if (page.Forbidden) return null;
        var ticket = page.Items.FirstOrDefault();
        if (ticket is null) return null;
        var comments = await ReadTicketRows(connection, "SELECT c.id,c.comment_type,c.body,c.created_at,u.display_name AS author_name FROM ticket_comments c JOIN users u ON u.id=c.author_user_id WHERE c.ticket_id=@ticket ORDER BY c.created_at,c.id", ticketId, ct);
        var history = await ReadTicketRows(connection, "SELECT h.id,h.action,h.previous_value,h.new_value,h.reason,h.created_at,COALESCE(u.display_name,'System') AS actor_name FROM ticket_history h LEFT JOIN users u ON u.id=h.actor_user_id WHERE h.ticket_id=@ticket ORDER BY h.created_at,h.id", ticketId, ct);
        var attachments = await ReadTicketRows(connection, "SELECT a.id,a.file_name,a.content_type,a.size_bytes,a.created_at,a.visibility,u.display_name AS uploaded_by FROM ticket_attachments a JOIN users u ON u.id=a.uploaded_by_user_id WHERE a.ticket_id=@ticket ORDER BY a.created_at", ticketId, ct);
        var approvals = await ReadTicketRows(connection, "SELECT a.id,a.status,a.reason,a.decision_note,a.created_at,a.decided_at,rq.display_name AS requested_by,dc.display_name AS decided_by FROM ticket_approvals a JOIN users rq ON rq.id=a.requested_by_user_id LEFT JOIN users dc ON dc.id=a.decided_by_user_id WHERE a.ticket_id=@ticket ORDER BY a.created_at DESC", ticketId, ct);
        var collaborators = await ReadTicketRows(connection, "SELECT c.user_id,u.display_name AS name,c.reason,c.created_at FROM ticket_collaborators c JOIN users u ON u.id=c.user_id WHERE c.ticket_id=@ticket ORDER BY u.display_name", ticketId, ct);
        return new { ticket, comments, history, attachments, approvals, collaborators, helpRequests = Array.Empty<object>(), knowledge = Array.Empty<object>(), problems = Array.Empty<object>() };
    }

    private static async Task<List<Dictionary<string, object?>>> ReadRows(MySqlConnection connection, string sql, string userId, CancellationToken ct)
    {
        await using var command = new MySqlCommand(sql, connection);
        if (sql.Contains("@id", StringComparison.Ordinal)) command.Parameters.AddWithValue("@id", userId);
        var rows = new List<Dictionary<string, object?>>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = ReadValue(reader, i);
            rows.Add(row);
        }
        return rows;
    }

    private static async Task<List<Dictionary<string, object?>>> ReadDashboardRows(MySqlConnection connection, string sql, string userId, CancellationToken ct)
    {
        await using var command = new MySqlCommand(sql, connection);
        if (sql.Contains("@id", StringComparison.Ordinal)) command.Parameters.AddWithValue("@id", userId);
        var rows = new List<Dictionary<string, object?>>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = ReadValue(reader, i);
            rows.Add(row);
        }
        return rows;
    }

    private static async Task<List<Dictionary<string, object?>>> ReadTicketRows(MySqlConnection connection, string sql, string ticketId, CancellationToken ct)
    {
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@ticket", ticketId);
        var rows = new List<Dictionary<string, object?>>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = ReadValue(reader, i);
            rows.Add(row);
        }
        return rows;
    }

    private static object? ReadValue(MySqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        var value = reader.GetValue(ordinal);
        if (value is string json && string.Equals(reader.GetDataTypeName(ordinal), "JSON", StringComparison.OrdinalIgnoreCase))
        {
            try { return System.Text.Json.JsonDocument.Parse(json).RootElement.Clone(); }
            catch (System.Text.Json.JsonException) { return json; }
        }
        return value;
    }

    private static async Task<bool> HasDefaultTeamPermission(MySqlConnection connection, string userId, CancellationToken ct)
    {
        const string sql = """
            SELECT ur.role_code, rp.permissions, COALESCE(uap.deny_permissions, JSON_ARRAY())
            FROM user_roles ur
            LEFT JOIN role_access_policies rp ON rp.role_code=ur.role_code
            LEFT JOIN user_access_policies uap ON uap.user_id=ur.user_id
            WHERE ur.user_id=@id AND ur.role_code IN ('TECHNICIAN','TEAM_LEAD','MANAGER','ADMIN')
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@id", userId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var roleGrant = false;
        while (await reader.ReadAsync(ct))
        {
            var role = reader.GetString(0);
            var defaults = role switch
            {
                "TECHNICIAN" => new[] { "TICKETS_TEAM_VIEW", "TICKETS_ASSIGN" },
                "TEAM_LEAD" => new[] { "TICKETS_TEAM_VIEW", "TICKETS_ASSIGN", "CATEGORY_MANAGE", "TAGS_MANAGE", "REPORTS_VIEW" },
                "MANAGER" or "ADMIN" => new[] { "TICKETS_TEAM_VIEW", "TICKETS_ASSIGN", "TICKETS_APPROVE", "CATEGORY_MANAGE", "TAGS_MANAGE", "SLA_MANAGE", "REPORTS_VIEW", "AUDIT_VIEW" },
                _ => Array.Empty<string>()
            };
            string[] permissions = defaults;
            if (!reader.IsDBNull(1))
            {
                try { permissions = System.Text.Json.JsonSerializer.Deserialize<string[]>(reader.GetString(1)) ?? []; }
                catch (System.Text.Json.JsonException) { permissions = []; }
            }
            string[] denied = [];
            try { denied = System.Text.Json.JsonSerializer.Deserialize<string[]>(reader.GetString(2)) ?? []; }
            catch (System.Text.Json.JsonException) { }
            if (permissions.Contains("TICKETS_TEAM_VIEW", StringComparer.Ordinal) && !denied.Contains("TICKETS_TEAM_VIEW", StringComparer.Ordinal)) roleGrant = true;
        }
        await reader.DisposeAsync();
        if (roleGrant) return true;
        await using var groupCommand = new MySqlCommand("SELECT EXISTS(SELECT 1 FROM staff_group_members gm JOIN access_groups g ON g.id=gm.group_id JOIN user_roles ur ON ur.user_id=gm.user_id LEFT JOIN user_access_policies p ON p.user_id=gm.user_id WHERE gm.user_id=@id AND g.kind='STAFF' AND g.active=1 AND ur.role_code IN ('TECHNICIAN','TEAM_LEAD','MANAGER','ADMIN') AND JSON_CONTAINS(g.permissions,JSON_QUOTE('TICKETS_TEAM_VIEW')) AND COALESCE(JSON_CONTAINS(p.deny_permissions,JSON_QUOTE('TICKETS_TEAM_VIEW')),0)=0 UNION SELECT 1 FROM user_access_policies p JOIN user_roles ur ON ur.user_id=p.user_id WHERE p.user_id=@id AND ur.role_code IN ('TECHNICIAN','TEAM_LEAD','MANAGER','ADMIN') AND JSON_CONTAINS(p.allow_permissions,JSON_QUOTE('TICKETS_TEAM_VIEW')) AND COALESCE(JSON_CONTAINS(p.deny_permissions,JSON_QUOTE('TICKETS_TEAM_VIEW')),0)=0)", connection);
        groupCommand.Parameters.AddWithValue("@id", userId);
        return Convert.ToInt32(await groupCommand.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static void AddTicketParameters(MySqlCommand command, string userId, string? status, string search, int pageSize, int page, string? ticketId)
    {
        command.Parameters.AddWithValue("@id", userId);
        command.Parameters.AddWithValue("@ticketId", (object?)ticketId ?? DBNull.Value);
        command.Parameters.AddWithValue("@status", (object?)status ?? DBNull.Value);
        command.Parameters.AddWithValue("@search", search);
        command.Parameters.AddWithValue("@pattern", $"%{search.Replace("%", "\\%").Replace("_", "\\_")}%");
        command.Parameters.AddWithValue("@take", pageSize);
        command.Parameters.AddWithValue("@skip", (page - 1) * pageSize);
    }

    public async Task<bool> SessionIsCurrent(string userId, int version, CancellationToken ct)
    {
        if (ConnectionString is null) return false;
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        await using var command = new MySqlCommand("SELECT 1 FROM users WHERE id=@id AND active=1 AND session_version=@version AND (access_expires_at IS NULL OR access_expires_at>UTC_TIMESTAMP(6))", connection);
        command.Parameters.AddWithValue("@id", userId);
        command.Parameters.AddWithValue("@version", version);
        return await command.ExecuteScalarAsync(ct) is not null;
    }

    public async Task<KnownAccount?> UpdateKnownAccount(string tenant, string objectId, string email, string displayName, CancellationToken ct)
    {
        if (ConnectionString is null) return null;
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var lookup = new MySqlCommand("SELECT id,session_version FROM users WHERE entra_tenant_id=@tenant AND entra_object_id=@oid AND active=1 AND (access_expires_at IS NULL OR access_expires_at>UTC_TIMESTAMP(6)) LIMIT 1 FOR UPDATE", connection, transaction);
        lookup.Parameters.AddWithValue("@tenant", tenant.ToLowerInvariant());
        lookup.Parameters.AddWithValue("@oid", objectId.ToLowerInvariant());
        await using var reader = await lookup.ExecuteReaderAsync(ct);
        KnownAccount? account = await reader.ReadAsync(ct) ? new KnownAccount(reader.GetString(0), reader.GetInt32(1)) : null;
        await reader.DisposeAsync();
        if (account is not null)
        {
            await using var update = new MySqlCommand("UPDATE users SET email=@email,display_name=@name,last_login_at=UTC_TIMESTAMP(6),updated_at=UTC_TIMESTAMP(6) WHERE id=@id", connection, transaction);
            update.Parameters.AddWithValue("@email", email);
            update.Parameters.AddWithValue("@name", displayName);
            update.Parameters.AddWithValue("@id", account.Id);
            await update.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return account;
    }

    public async Task<object?> GetUserView(string id, string workspace, CancellationToken ct)
    {
        if (ConnectionString is null) return null;
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        await using var command = new MySqlCommand("SELECT id,display_name,email,work_scope,change_access FROM users WHERE id=@id AND active=1 AND (access_expires_at IS NULL OR access_expires_at>UTC_TIMESTAMP(6))", connection);
        command.Parameters.AddWithValue("@id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var userId = reader.GetString(0);
        var displayName = reader.GetString(1);
        var email = reader.GetString(2);
        var scope = reader.GetString(3);
        var changeAccess = reader.GetString(4);
        await reader.DisposeAsync();
        var rawRoles = await ReadStrings(connection, "SELECT role_code FROM user_roles WHERE user_id=@id ORDER BY role_code", id, ct);
        var clientIds = await ReadStrings(connection, "SELECT client_id FROM client_users WHERE user_id=@id ORDER BY client_id", id, ct);
        var siteIds = await ReadStrings(connection, "SELECT site_id FROM user_sites WHERE user_id=@id ORDER BY site_id", id, ct);
        var departmentIds = await ReadStrings(connection, "SELECT department_id FROM department_users WHERE user_id=@id ORDER BY department_id", id, ct);
        var admin = rawRoles.Contains("ADMIN", StringComparer.Ordinal);
        var groups = await ReadJsonPermissionList(connection, "SELECT DISTINCT g.permissions FROM access_groups g JOIN staff_group_members gm ON gm.group_id=g.id WHERE gm.user_id=@id AND g.kind='STAFF' AND g.active=1", id, ct);
        if (workspace == "chooser") return new { id = userId, displayName, email, roles = rawRoles, clientIds, siteIds, permissions = admin ? new[] { "*" } : Array.Empty<string>(), groupPermissions = groups, workScope = admin ? "ALL" : scope, changeAccess = admin ? "CHANGE_MANAGER" : changeAccess, departmentIds, workspace = (string?)null };
        var chosen = workspace == "default" ? admin ? "operations" : rawRoles.Any(r => r is "MANAGER" or "TEAM_LEAD") ? "management" : rawRoles.Any(r => r is "INTERN" or "TECHNICIAN") ? "operations" : "client" : workspace;
        string[] roles;
        string effectiveScope = admin ? "ALL" : scope;
        string effectiveChange = admin ? "CHANGE_MANAGER" : changeAccess;
        if (chosen == "admin")
        {
            if (!admin) return null;
            roles = rawRoles;
        }
        else if (chosen == "client")
        {
            if (!rawRoles.Contains("USER", StringComparer.Ordinal)) return null;
            roles = ["USER"];
            groups = [];
            effectiveScope = "CLIENT";
            effectiveChange = "NONE";
        }
        else if (chosen == "management")
        {
            if (!rawRoles.Any(r => r is "ADMIN" or "MANAGER" or "TEAM_LEAD")) return null;
            roles = [admin || rawRoles.Contains("MANAGER", StringComparer.Ordinal) ? "MANAGER" : "TEAM_LEAD"];
            if (admin) effectiveChange = "COORDINATOR";
        }
        else if (chosen == "operations")
        {
            if (!rawRoles.Any(r => r is "ADMIN" or "MANAGER" or "TEAM_LEAD" or "TECHNICIAN" or "INTERN")) return null;
            roles = [rawRoles.Any(r => r is "ADMIN" or "MANAGER" or "TEAM_LEAD" or "TECHNICIAN") ? "TECHNICIAN" : "INTERN"];
            if (roles[0] == "INTERN") groups = [];
            effectiveChange = effectiveChange == "NONE" ? "NONE" : "CONTRIBUTOR";
        }
        else return null;
        var permissions = await ResolvePermissions(connection, userId, roles[0], admin, groups, ct);
        var effectiveClients = admin && (chosen is "operations" or "management") ? await ReadStrings(connection, "SELECT id FROM clients ORDER BY id", id, ct) : clientIds;
        var effectiveSites = admin && (chosen is "operations" or "management") ? Array.Empty<string>() : siteIds;
        return new { id = userId, displayName, email, roles, clientIds = effectiveClients, siteIds = effectiveSites, permissions, groupPermissions = groups, workScope = effectiveScope, changeAccess = effectiveChange, departmentIds, workspace = chosen };
    }

    private static readonly string[] StaffPermissions = ["TICKETS_TEAM_VIEW", "TICKETS_ASSIGN", "TICKETS_APPROVE", "CATEGORY_MANAGE", "SLA_MANAGE", "REPORTS_VIEW", "TAGS_MANAGE"];
    private static async Task<string[]> ReadJsonPermissionList(MySqlConnection connection, string sql, string id, CancellationToken ct)
    {
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var values = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(ct))
        {
            try { foreach (var permission in System.Text.Json.JsonSerializer.Deserialize<string[]>(reader.GetString(0)) ?? []) values.Add(permission); }
            catch (System.Text.Json.JsonException) { }
        }
        return values.Order(StringComparer.Ordinal).ToArray();
    }
    private static async Task<string[]> ResolvePermissions(MySqlConnection connection, string userId, string role, bool isAdmin, string[] groupPermissions, CancellationToken ct)
    {
        if (role == "ADMIN") return ["*"];
        var defaults = role switch
        {
            "TECHNICIAN" => new[] { "TICKETS_TEAM_VIEW", "TICKETS_ASSIGN" },
            "TEAM_LEAD" => new[] { "TICKETS_TEAM_VIEW", "TICKETS_ASSIGN", "CATEGORY_MANAGE", "TAGS_MANAGE", "REPORTS_VIEW" },
            "MANAGER" => new[] { "TICKETS_TEAM_VIEW", "TICKETS_ASSIGN", "TICKETS_APPROVE", "CATEGORY_MANAGE", "TAGS_MANAGE", "SLA_MANAGE", "REPORTS_VIEW", "AUDIT_VIEW" },
            _ => Array.Empty<string>()
        };
        var ceiling = role is "MANAGER" or "TEAM_LEAD" ? StaffPermissions.Append("AUDIT_VIEW").ToHashSet(StringComparer.Ordinal) : role == "TECHNICIAN" ? StaffPermissions.ToHashSet(StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
        await using (var rolePolicyCommand = new MySqlCommand("SELECT permissions FROM role_access_policies WHERE role_code=@id", connection))
        {
            rolePolicyCommand.Parameters.AddWithValue("@id", role);
            var storedPolicy = await rolePolicyCommand.ExecuteScalarAsync(ct);
            if (storedPolicy is string json)
            {
                try { defaults = System.Text.Json.JsonSerializer.Deserialize<string[]>(json) ?? []; }
                catch (System.Text.Json.JsonException) { defaults = []; }
            }
        }
        var allowedByUser = isAdmin ? Array.Empty<string>() : await ReadJsonPermissionList(connection, "SELECT allow_permissions FROM user_access_policies WHERE user_id=@id", userId, ct);
        var deniedByUser = isAdmin ? Array.Empty<string>() : await ReadJsonPermissionList(connection, "SELECT deny_permissions FROM user_access_policies WHERE user_id=@id", userId, ct);
        var denied = deniedByUser.ToHashSet(StringComparer.Ordinal);
        return defaults.Concat(groupPermissions.Where(StaffPermissions.Contains)).Concat(allowedByUser)
            .Where(p => ceiling.Contains(p) && !denied.Contains(p)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static async Task<string[]> ReadStrings(MySqlConnection connection, string sql, string id, CancellationToken ct)
    {
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var values = new List<string>();
        while (await reader.ReadAsync(ct)) values.Add(reader.GetString(0));
        return values.ToArray();
    }
}

public sealed record KnownAccount(string Id, int SessionVersion);
public sealed record TicketPage(int Total, IReadOnlyList<Dictionary<string, object?>> Items, bool Forbidden);
public sealed record CreateRequesterRequest(string ClientId, string Name, string? Email, string? Phone);
public sealed record CreatedRequester(string Id, string ClientId, string Name, string? Email);
public sealed class CreateTicketRequest
{
    public string ClientId { get; init; } = "";
    public string RequesterId { get; init; } = "";
    public string? SiteId { get; init; }
    public string? DepartmentId { get; init; }
    public string? LocationText { get; init; }
    public string CategoryId { get; init; } = "";
    public string? AssignedUserId { get; init; }
    public string Subject { get; init; } = "";
    public string Description { get; init; } = "";
    public string Source { get; init; } = "";
    public string Priority { get; init; } = "P3_MEDIUM";
    public string? SourceReferenceId { get; init; }
    public string[]? TagIds { get; init; } = [];
    public CreateAttachment[]? Attachments { get; init; } = [];
}
public sealed class CreateAttachment
{
    public string FileName { get; init; } = "";
    public string ContentType { get; init; } = "application/octet-stream";
    public string ContentBase64 { get; init; } = "";
    public string? Visibility { get; init; }
}
public sealed record CreatedTicket(string Id, string TicketNumber);
public sealed class CreateTicketCommentRequest
{
    public string Type { get; init; } = "";
    public string Body { get; init; } = "";
}
public sealed record CreatedTicketComment(string Id, string Type, string Body);
public sealed class AssignTicketRequest
{
    public string AssignedUserId { get; init; } = "";
    public string? Reason { get; init; }
}
public sealed record AssignedTicket(string Id, string AssignedUserId);
public sealed class ApprovalRequest { public string Reason { get; init; } = ""; }
public sealed class ApprovalDecision { public string Decision { get; init; } = ""; public string Note { get; init; } = ""; }
public sealed record CreatedApproval(string Id);
public sealed record ApprovalDecisionResult(string Id, string Decision);
public sealed record CreatedTicketAttachment(string Id);
public sealed record TicketAttachmentFile(string FileName, string ContentType, byte[] Content);
public sealed class CreateContactRequest { public string ClientId { get; init; } = ""; public string? SiteId { get; init; } public string Name { get; init; } = ""; public string? Email { get; init; } public string? Phone { get; init; } }
public sealed class CreateClientRequest { public string Name { get; init; } = ""; public string? TicketPrefix { get; init; } public string? PrimaryEmail { get; init; } public string? PrimaryPhone { get; init; } public string? Notes { get; init; } }
public sealed class CreateSiteRequest { public string ClientId { get; init; } = ""; public string Name { get; init; } = ""; public string? TicketPrefix { get; init; } public string? Address { get; init; } }
public sealed class CreateAssetRequest { public string ClientId { get; init; } = ""; public string? SiteId { get; init; } public string Name { get; init; } = ""; public string AssetType { get; init; } = ""; public string? SerialNumber { get; init; } public string? ExternalReference { get; init; } }
public sealed class SlaCalendarRequest { public string Name { get; init; } = ""; public string Timezone { get; init; } = ""; public List<int> WorkingDays { get; init; } = []; public string WorkStart { get; init; } = ""; public string WorkEnd { get; init; } = ""; public bool Active { get; init; } public List<SlaHolidayRequest> Holidays { get; init; } = []; }
public sealed class SlaPolicyRequest { public int ResponseMinutes { get; init; } public int ResolutionMinutes { get; init; } public int WarningPercent { get; init; } public bool Active { get; init; } public string CalendarId { get; init; } = ""; public bool ApplyToOpenTickets { get; init; } }
public sealed class SlaHolidayRequest { public string Date { get; init; } = ""; public string Name { get; init; } = ""; }
public sealed class RolePolicyRequest { public int Revision { get; init; } public List<string> Permissions { get; init; } = []; }
public sealed class UserPolicyRequest { public int Revision { get; init; } public List<string> Allow { get; init; } = []; public List<string> Deny { get; init; } = []; }
public sealed class NotificationPreferencesRequest { public bool TicketAssigned { get; init; } = true; public bool TicketTransferred { get; init; } = true; public bool PublicReply { get; init; } = true; public bool SlaAtRisk { get; init; } = true; public bool SlaBreached { get; init; } = true; public bool TicketUnattended { get; init; } = true; }
public sealed class DepartmentRequest { public string Name { get; init; } = ""; public string? Description { get; init; } }
public sealed class DepartmentMemberRequest { public string UserId { get; init; } = ""; public string? Expertise { get; init; } public bool IsLead { get; init; } }
public sealed class ServiceTemplateRequest { public string Name { get; init; } = ""; public string Description { get; init; } = ""; public string Instructions { get; init; } = ""; public string CategoryId { get; init; } = ""; public string? ClientId { get; init; } public bool Active { get; init; } = true; }
public sealed class KnowledgeArticleRequest { public string Title { get; init; } = ""; public string Body { get; init; } = ""; public string? CategoryId { get; init; } public string? ClientId { get; init; } public string Visibility { get; init; } = "STAFF"; public string Status { get; init; } = "DRAFT"; public int? Revision { get; init; } }
public sealed class KnowledgeTicketLinkRequest { public string TicketId { get; init; } = ""; }
public sealed class CreateProblemRequest { public string ClientId { get; init; } = ""; public string? SiteId { get; init; } public string Title { get; init; } = ""; public string Description { get; init; } = ""; public string Priority { get; init; } = "P3_MEDIUM"; }
public sealed class ProblemTicketRequest { public string TicketId { get; init; } = ""; }
public sealed class CreateCategoryRequest { public string? ParentId { get; init; } public string Name { get; init; } = ""; public string? Description { get; init; } }
public sealed class CreateTagRequest { public string Name { get; init; } = ""; public string? Description { get; init; } }
public sealed class TicketSourceRequest { public string Code { get; init; } = ""; public string Name { get; init; } = ""; }
public sealed class UpdateTicketSourceRequest { public string Name { get; init; } = ""; public bool Active { get; init; } }
public sealed record CreatedContact(string Id);
public sealed record CreatedEntity(string Id);
public sealed record TicketSourceView(string Code,string Name,bool Active,int SortOrder);
public sealed class SaveTicketViewRequest { public string Name { get; init; } = ""; public Dictionary<string, string?> Query { get; init; } = new(StringComparer.Ordinal); }
public sealed class UpdateTicketRequest
{
    public string? Status { get; init; }
    public string? Priority { get; init; }
    public string? CategoryId { get; init; }
    public string[]? TagIds { get; init; }
    public string? Reason { get; init; }
}
public sealed record UpdatedTicket(string Id);
public sealed record SlaCalendar(string PolicyId, string Id, int ResponseMinutes, int ResolutionMinutes, int WarningPercent, TimeZoneInfo Zone, HashSet<int> WorkingDays, TimeSpan WorkStart, TimeSpan WorkEnd, HashSet<DateOnly> Holidays);
public static class SlaBusinessCalendar
{
    public static DateTime Add(SlaCalendar calendar, DateTime startUtc, int requestedMinutes)
    {
        if (requestedMinutes <= 0) return DateTime.SpecifyKind(startUtc, DateTimeKind.Utc);
        var cursor = DateTime.SpecifyKind(startUtc, DateTimeKind.Utc);
        var localDate = TimeZoneInfo.ConvertTimeFromUtc(cursor, calendar.Zone).Date;
        var remaining = requestedMinutes;
        for (var guard = 0; guard < 3700; guard++, localDate = localDate.AddDays(1))
        {
            var date = DateOnly.FromDateTime(localDate);
            if (!calendar.WorkingDays.Contains(IsoDay(localDate)) || calendar.Holidays.Contains(date)) continue;
            var dayStart = ToUtc(localDate.Add(calendar.WorkStart), calendar.Zone);
            var dayEnd = ToUtc(localDate.Add(calendar.WorkEnd), calendar.Zone);
            if (cursor < dayStart) cursor = dayStart;
            if (cursor >= dayEnd) continue;
            var available = (int)Math.Floor((dayEnd - cursor).TotalMinutes);
            if (remaining <= available) return cursor.AddMinutes(remaining);
            remaining -= available;
            cursor = ToUtc(localDate.AddDays(1), calendar.Zone);
        }
        throw new InvalidOperationException("SLA calendar cannot satisfy the deadline.");
    }

    public static int Between(SlaCalendar calendar, DateTime startUtc, DateTime endUtc)
    {
        var start = DateTime.SpecifyKind(startUtc, DateTimeKind.Utc);
        var end = DateTime.SpecifyKind(endUtc, DateTimeKind.Utc);
        if (end <= start) return 0;
        var firstDate = TimeZoneInfo.ConvertTimeFromUtc(start, calendar.Zone).Date;
        var lastDate = TimeZoneInfo.ConvertTimeFromUtc(end, calendar.Zone).Date;
        var total = 0;
        for (var localDate = firstDate; localDate <= lastDate; localDate = localDate.AddDays(1))
        {
            var date = DateOnly.FromDateTime(localDate);
            if (!calendar.WorkingDays.Contains(IsoDay(localDate)) || calendar.Holidays.Contains(date)) continue;
            var dayStart = ToUtc(localDate.Add(calendar.WorkStart), calendar.Zone);
            var dayEnd = ToUtc(localDate.Add(calendar.WorkEnd), calendar.Zone);
            var overlapStart = start > dayStart ? start : dayStart;
            var overlapEnd = end < dayEnd ? end : dayEnd;
            if (overlapEnd > overlapStart) total = checked(total + (int)Math.Floor((overlapEnd - overlapStart).TotalMinutes));
        }
        return total;
    }

    private static int IsoDay(DateTime localDate) => ((int)localDate.DayOfWeek + 6) % 7 + 1;
    private static DateTime ToUtc(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        for (var attempt = 0; zone.IsInvalidTime(local) && attempt < 180; attempt++) local = local.AddMinutes(1);
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }
}
