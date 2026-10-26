namespace Astra.Api.Configuration;

public sealed class IdentityOptions
{
    public required Uri BaseUri { get; init; }
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public string? TenantId { get; init; }
    public int SessionSeconds { get; init; }
    public required IReadOnlySet<Guid> InitialAdministrators { get; init; }
    public bool Configured => ClientId is not null && ClientSecret is not null && TenantId is not null;
    public string Origin => BaseUri.GetLeftPart(UriPartial.Authority);

    public static IdentityOptions Read(IConfiguration config, IHostEnvironment environment)
    {
        var value = config["APP_BASE_URL"] ?? "http://localhost:3000";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || uri.UserInfo.Length > 0 || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new InvalidOperationException("Invalid configuration: APP_BASE_URL");
        var client = NullEmpty(config["ENTRA_CLIENT_ID"]);
        var secret = NullEmpty(config["ENTRA_CLIENT_SECRET"]);
        var tenant = NullEmpty(config["ENTRA_TENANT_ID"]);
        var supplied = new[] { client, secret, tenant }.Count(v => v is not null);
        if (supplied != 0 && (supplied != 3 || !Guid.TryParse(client, out _) || !Guid.TryParse(tenant, out _) || secret!.Length < 16))
            throw new InvalidOperationException("Invalid configuration: ENTRA settings");
        if (supplied == 3 && environment.IsProduction() && uri.Scheme != "https")
            throw new InvalidOperationException("Invalid configuration: production identity requires HTTPS");
        var ttl = 28800;
        if (config["SESSION_TTL_SECONDS"] is string configured && (!int.TryParse(configured, out ttl) || ttl is < 900 or > 43200))
            throw new InvalidOperationException("Invalid configuration: SESSION_TTL_SECONDS");
        var administrators = new HashSet<Guid>();
        foreach (var entry in (config["INITIAL_ADMIN_OBJECT_IDS"] ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Guid.TryParse(entry, out var id)) throw new InvalidOperationException("Invalid configuration: INITIAL_ADMIN_OBJECT_IDS");
            administrators.Add(id);
        }
        return new IdentityOptions { BaseUri = uri, ClientId = client, ClientSecret = secret, TenantId = tenant, SessionSeconds = ttl, InitialAdministrators = administrators };
    }

    private static string? NullEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    public static string SafeReturnTo(string? path) => path is "/client" or "/operations" or "/management" or "/admin" ? path : "/";
    public static string Destination(IReadOnlySet<string> roles, string? path) => SafeReturnTo(path) switch
    {
        "/client" when roles.Contains("USER") => "/client",
        "/admin" when roles.Contains("ADMIN") => "/admin",
        "/operations" when roles.Overlaps(["ADMIN", "MANAGER", "TEAM_LEAD", "TECHNICIAN", "INTERN"]) => "/operations",
        "/management" when roles.Overlaps(["ADMIN", "MANAGER", "TEAM_LEAD"]) => "/management",
        _ => "/"
    };
}
