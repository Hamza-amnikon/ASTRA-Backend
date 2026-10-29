using System.Net.Http.Headers;
using System.Text.Json;

public sealed record DirectoryIdentity(string Id, string DisplayName, string Email, string UserPrincipalName, string TenantId);
public interface IIdentityDirectory
{
    Task<DirectoryIdentity[]> SearchAsync(string query, CancellationToken ct);
    Task<DirectoryIdentity> FindAsync(string objectId, CancellationToken ct);
}
public sealed class GraphIdentityDirectory(IConfiguration config) : IIdentityDirectory, IDisposable
{
    private readonly HttpClient client = new() { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 1024 * 1024 };
    private async Task<JsonDocument> Read(string suffix, CancellationToken ct)
    {
        var auth = AuthSettings.Load(config);
        if (!Guid.TryParse(auth.TenantId, out var tenant) || !Guid.TryParse(auth.ClientId, out _) || string.IsNullOrWhiteSpace(auth.ClientSecret))
            throw new FeatureFailure(503, "DIRECTORY_NOT_CONFIGURED", "Adding Microsoft accounts requires Entra application credentials and directory-read permission. Existing local test access remains available.");
        try
        {
            using var response = await client.PostAsync($"https://login.microsoftonline.com/{tenant:D}/oauth2/v2.0/token", new FormUrlEncodedContent(new Dictionary<string, string> { { "client_id", auth.ClientId }, { "client_secret", auth.ClientSecret }, { "scope", "https://graph.microsoft.com/.default" }, { "grant_type", "client_credentials" } }), ct);
            if (!response.IsSuccessStatusCode)
                throw new FeatureFailure(502, "DIRECTORY_AUTH_FAILED", "Microsoft directory authentication failed. Check the Entra application settings.");
            using var token = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/users" + suffix);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.RootElement.GetProperty("access_token").GetString());
            using var result = await client.SendAsync(request, ct);
            if (result.StatusCode == System.Net.HttpStatusCode.NotFound)
                throw new KeyNotFoundException();
            if (!result.IsSuccessStatusCode)
                throw new FeatureFailure(502, "DIRECTORY_READ_FAILED", "Microsoft directory access failed. Check directory-read permission and administrator consent.");
            return JsonDocument.Parse(await result.Content.ReadAsStringAsync(ct));
        }
        catch (HttpRequestException) { throw new FeatureFailure(502, "DIRECTORY_UNREACHABLE", "The Microsoft directory could not be reached. Try again later."); }
    }
    private DirectoryIdentity Identity(JsonElement row)
    {
        if (row.TryGetProperty("accountEnabled", out var enabled) && enabled.ValueKind == JsonValueKind.False)
            throw new ArgumentException("This Microsoft account is disabled.");
        var id = row.GetProperty("id").GetString();
        if (!Guid.TryParse(id, out _))
            throw new ArgumentException("Microsoft returned an invalid account identifier.");
        var principal = row.GetProperty("userPrincipalName").GetString() ?? "";
        var email = row.TryGetProperty("mail", out var mail) && mail.ValueKind == JsonValueKind.String ? mail.GetString() : null;
        email = string.IsNullOrWhiteSpace(email) ? principal : email;
        if (!System.Net.Mail.MailAddress.TryCreate(email, out _))
            throw new ArgumentException("The Microsoft account has no valid email address.");
        return new(id!, row.GetProperty("displayName").GetString() ?? principal, email!.ToLowerInvariant(), principal, AuthSettings.Load(config).TenantId);
    }
    public async Task<DirectoryIdentity[]> SearchAsync(string query, CancellationToken ct)
    {
        if (Guid.TryParse(query, out var id))
            return [await FindAsync(id.ToString("D"), ct)];
        var escaped = query.Replace("'", "''");
        var filter = string.Join(" or ", new[] { "displayName", "givenName", "surname", "userPrincipalName", "mail" }.Select(field => $"startsWith({field},'{escaped}')"));
        using var json = await Read("?$top=10&$select=id,displayName,mail,userPrincipalName,accountEnabled&$filter=" + Uri.EscapeDataString(filter), ct);
        return json.RootElement.GetProperty("value").EnumerateArray().Where(x => !x.TryGetProperty("accountEnabled", out var enabled) || enabled.ValueKind != JsonValueKind.False).Select(Identity).ToArray();
    }
    public async Task<DirectoryIdentity> FindAsync(string objectId, CancellationToken ct)
    {
        if (!Guid.TryParse(objectId, out var id))
            throw new ArgumentException("Select a valid Microsoft account.");
        using var json = await Read("/" + id.ToString("D") + "?$select=id,displayName,mail,userPrincipalName,accountEnabled", ct);
        return Identity(json.RootElement);
    }
    public void Dispose() => client.Dispose();
}
public static partial class FeatureApis
{
    private static void MapIdentityDirectory(RouteGroupBuilder api)
    {
        api.MapPost("/users/resolve", async (JsonElement body, HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.ResolveDirectoryUser(Actor(c), body, t)));
        api.MapPost("/users", async (JsonElement body, HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.ProvisionDirectoryUser(Actor(c), body, t)));
    }
}
public sealed partial class MySqlStore
{
    public async Task<object> ResolveDirectoryUser(string actor, JsonElement body, CancellationToken ct)
    {
        await RequireAdmin(actor, ct);
        var query = FText(JString(body, "identifier"), "Search", 3, 200);
        if (identityDirectory is null)
            throw new FeatureFailure(503, "DIRECTORY_NOT_CONFIGURED", "Microsoft directory access is not configured.");
        return new
        {
            items = await identityDirectory.SearchAsync(query, ct)
        };
    }
    public async Task<object> ProvisionDirectoryUser(string actor, JsonElement body, CancellationToken ct)
    {
        await RequireAdmin(actor, ct);
        var objectId = FGuid(JString(body, "entraObjectId"), "Microsoft account");
        if (identityDirectory is null)
            throw new FeatureFailure(503, "DIRECTORY_NOT_CONFIGURED", "Microsoft directory access is not configured.");
        var account = await identityDirectory.FindAsync(objectId, ct);
        if (account.Id != objectId || !Guid.TryParse(account.TenantId, out _))
            throw new ArgumentException("The verified Microsoft identity is invalid.");
        return await UpdateUserAccess(actor, Guid.NewGuid().ToString("D"), body, ct, account);
    }
}
