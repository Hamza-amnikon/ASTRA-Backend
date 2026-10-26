using System.Net.Http.Json;
using System.Text.Json;
using Astra.Api.Data;

namespace Astra.Api.Services;

public sealed record DirectoryUser(Guid Id, string DisplayName, string Email, string UserPrincipalName);

public sealed class EntraDirectory(HttpClient http, IdentityOptions identity)
{
    private readonly SemaphoreSlim tokenLock = new(1, 1);
    private string? cachedToken;
    private DateTimeOffset tokenExpiresAt;

    public async Task<IReadOnlyList<DirectoryUser>> SearchAsync(string identifier, CancellationToken cancellationToken)
    {
        var term = identifier.Trim();
        if (term.Length is < 1 or > 200) throw new ApiFault(400, "Enter a name, work email or Entra UPN.");
        if (!identity.Configured) throw new ApiFault(503, "Identity provider not configured.");

        var escaped = term.Replace("'", "''", StringComparison.Ordinal);
        var filter = string.Join(" or ",
        [
            $"startsWith(displayName,'{escaped}')",
            $"startsWith(givenName,'{escaped}')",
            $"startsWith(surname,'{escaped}')",
            $"startsWith(userPrincipalName,'{escaped}')",
            $"startsWith(mail,'{escaped}')"
        ]);
        var query = "$filter=" + Uri.EscapeDataString(filter)
            + "&$select=id,displayName,mail,userPrincipalName&$top=10";
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/users?" + query);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", await AccessTokenAsync(cancellationToken));
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new ApiFault(response.StatusCode == System.Net.HttpStatusCode.NotFound ? 404 : 503,
            response.StatusCode == System.Net.HttpStatusCode.NotFound ? "Directory user not found." : "Directory lookup is unavailable.");
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("value", out var values) || values.ValueKind != JsonValueKind.Array)
            throw new ApiFault(503, "Directory lookup is unavailable.");
        var results = new List<DirectoryUser>();
        foreach (var item in values.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var idValue) || !Guid.TryParse(idValue.GetString(), out var id)
                || !item.TryGetProperty("displayName", out var nameValue) || nameValue.GetString() is not { } name
                || !item.TryGetProperty("userPrincipalName", out var upnValue) || upnValue.GetString() is not { } upn)
                throw new ApiFault(503, "Directory lookup returned an invalid user record.");
            var email = item.TryGetProperty("mail", out var mailValue) ? mailValue.GetString() : null;
            results.Add(new DirectoryUser(id, name, string.IsNullOrWhiteSpace(email) ? upn : email, upn));
        }
        return results;
    }

    public async Task<DirectoryUser> FindByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!identity.Configured) throw new ApiFault(503, "Identity provider not configured.");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://graph.microsoft.com/v1.0/users/{id:D}?$select=id,displayName,mail,userPrincipalName");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", await AccessTokenAsync(cancellationToken));
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new ApiFault(response.StatusCode == System.Net.HttpStatusCode.NotFound ? 404 : 503,
            response.StatusCode == System.Net.HttpStatusCode.NotFound ? "Directory user not found." : "Directory lookup is unavailable.");
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var item = document.RootElement;
        if (!item.TryGetProperty("id", out var idValue) || !Guid.TryParse(idValue.GetString(), out var returnedId) || returnedId != id
            || !item.TryGetProperty("displayName", out var nameValue) || nameValue.GetString() is not { Length: > 0 } name
            || !item.TryGetProperty("userPrincipalName", out var upnValue) || upnValue.GetString() is not { Length: > 0 } upn)
            throw new ApiFault(503, "Directory lookup returned an invalid user record.");
        var email = item.TryGetProperty("mail", out var mailValue) ? mailValue.GetString() : null;
        return new DirectoryUser(id, name, string.IsNullOrWhiteSpace(email) ? upn : email, upn);
    }

    private async Task<string> AccessTokenAsync(CancellationToken cancellationToken)
    {
        if (cachedToken is not null && tokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return cachedToken;
        await tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (cachedToken is not null && tokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return cachedToken;
            using var response = await http.PostAsync(
                $"https://login.microsoftonline.com/{identity.TenantId}/oauth2/v2.0/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = identity.ClientId!,
                    ["client_secret"] = identity.ClientSecret!,
                    ["grant_type"] = "client_credentials",
                    ["scope"] = "https://graph.microsoft.com/.default"
                }), cancellationToken);
            if (!response.IsSuccessStatusCode) throw new ApiFault(503, "Directory lookup is unavailable.");
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("access_token", out var token) || token.GetString() is not { Length: > 0 } value)
                throw new ApiFault(503, "Directory lookup is unavailable.");
            var expires = document.RootElement.TryGetProperty("expires_in", out var expiry) && expiry.TryGetInt32(out var seconds) ? seconds : 3600;
            cachedToken = value;
            tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expires - 120));
            return cachedToken;
        }
        finally { tokenLock.Release(); }
    }
}
