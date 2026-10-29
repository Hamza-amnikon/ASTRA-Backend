using System.Security.Claims;
using System.Text.Json;
using MySqlConnector;

public static partial class FeatureApis
{
    public static void MapFeatureApis(this WebApplication app, Func<HttpContext, AuthSettings, bool> validCsrf)
    {
        var api = app.MapGroup("/api").RequireAuthorization().AddEndpointFilter<FeatureErrors>();
        api.AddEndpointFilter(async (invocation, next) =>
        {
            var c = invocation.HttpContext;
            if (c.Request.Method is not ("GET" or "HEAD" or "OPTIONS") &&
                !validCsrf(c, c.RequestServices.GetRequiredService<AuthSettings>()))
                return Error(403, "INVALID_CSRF", "Your session could not be verified. Refresh and try again.");
            return await next(invocation);
        });
        MapGroups(api);
        MapReports(api);
        MapChanges(api);
        MapChangeGovernance(api);
        MapMail(api);
        MapDomains(api);
        MapAdminEdits(api);
        MapProblemEdits(api);
        MapIdentityDirectory(api);
        MapPortal(api);
        MapHelp(api);
        MapSupport(app);
    }

    internal static string Actor(HttpContext c) => c.User.FindFirstValue("astra-user-id") ?? throw new UnauthorizedAccessException();
    internal static IResult Error(int status, string code, string message) => Results.Json(new { error = new { code, message } }, statusCode: status);

    private static void MapGroups(RouteGroupBuilder api)
    {
        api.MapGet("/admin/groups/options", async (HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.GroupOptions(Actor(c), t)));
        api.MapGet("/admin/groups", async (HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.Groups(Actor(c), c.Request.Query["q"], c.Request.Query["kind"], c.Request.Query["page"], t)));
        api.MapGet("/admin/groups/{id:guid}", async (string id, HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.Group(Actor(c), id, t)));
        api.MapPost("/admin/groups", async (GroupInput b, HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.SaveGroup(Actor(c), null, b, t)));
        api.MapPatch("/admin/groups/{id:guid}", async (string id, GroupInput b, HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.SaveGroup(Actor(c), id, b, t)));
    }
}

public sealed class FeatureErrors : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext c, EndpointFilterDelegate next)
    {
        try
        {
            if (c.Arguments.OfType<JsonElement>().Any(b => b.ValueKind != JsonValueKind.Object))
                return FeatureApis.Error(400, "INVALID_JSON", "Send a record object with the required fields.");
            return await next(c);
        }
        catch (FeatureFailure e) { return FeatureApis.Error(e.Status, e.Code, e.Message); }
        catch (UnauthorizedAccessException) { return FeatureApis.Error(403, "FORBIDDEN", "Your account does not have access to this action."); }
        catch (KeyNotFoundException) { return FeatureApis.Error(404, "NOT_FOUND", "The requested record was not found."); }
        catch (ArgumentException e) { return FeatureApis.Error(400, "INVALID_REQUEST", e.Message); }
        catch (JsonException) { return FeatureApis.Error(400, "INVALID_JSON", "The request data is invalid."); }
        catch (InvalidOperationException e) { return FeatureApis.Error(409, "CONFLICT", e.Message); }
        catch (MySqlException e) when (e.Number == 1062) { return FeatureApis.Error(409, "DUPLICATE", "A record with these values already exists."); }
        catch (MySqlException e) when (e.Number is 1451 or 1452 or 1644 or 3819 or 1406)
        {
            return FeatureApis.Error(400, "INVALID_REFERENCE", "The record has invalid values or relationships. Check your selections and try again.");
        }
    }
}

public sealed class FeatureFailure(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status; public string Code { get; } = code;
}

public sealed class GroupInput
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Kind { get; init; } = "";
    public string? ClientId
    {
        get; init;
    }
    public string[] Permissions { get; init; } = [];
    public string[] MemberIds { get; init; } = [];
    public bool Active { get; init; } = true;
    public int? Revision
    {
        get; init;
    }
}
