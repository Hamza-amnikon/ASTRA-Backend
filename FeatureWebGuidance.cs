using System.Text.Json;
using System.Text.RegularExpressions;

public sealed record WebGuidanceStatus(bool Enabled, bool Configured, string Provider, string Note);
public sealed record WebGuidanceResult(string Title, string Href, string Source, string[] Excerpts);
public sealed partial class MySqlStore
{
    private static readonly HttpClient GuidanceClient = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 2 * 1024 * 1024 };
    public async Task<WebGuidanceStatus> HelpWebStatus(string actor, CancellationToken ct)
    {
        var roles = await FQuery("SELECT role_code FROM user_roles WHERE user_id=@actor", ct, ("actor", actor));
        if (!roles.Any(x => (string?)x["role_code"] is "TECHNICIAN" or "TEAM_LEAD" or "MANAGER" or "ADMIN"))
            throw new UnauthorizedAccessException();
        var provider = configuration["WEB_GUIDANCE_PROVIDER"] ?? "brave";
        var configured = provider == "brave" ? !string.IsNullOrWhiteSpace(configuration["BRAVE_SEARCH_API_KEY"]) : provider == "searxng" && Uri.TryCreate(configuration["WEB_GUIDANCE_SEARXNG_URL"], UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
        var enabled = configuration.GetValue("WEB_GUIDANCE_ENABLED", false) && configured;
        return new(enabled, configured, provider, enabled ? "Only general troubleshooting queries are sent to the configured search provider." : "Approved web search is not configured. Local guides and knowledge search are available.");
    }
    private static Uri? GuidanceUrl(string? text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length > 0 || uri.IsLoopback || System.Net.IPAddress.TryParse(uri.Host, out _) || uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) || !uri.Host.Contains('.'))
            return null;
        return uri;
    }
    public async Task<object> HelpWebSearch(string actor, JsonElement body, CancellationToken ct)
    {
        var status = await HelpWebStatus(actor, ct);
        if (!status.Enabled)
            throw new FeatureFailure(503, "WEB_SEARCH_NOT_CONFIGURED", status.Note);
        var query = FText(JString(body, "query"), "Query", 3, 500);
        if (SafeHelpQuery(query) != query || Regex.IsMatch(query, @"(?:password|secret|token|api[_ -]?key)\s*[:=]", RegexOptions.IgnoreCase))
            throw new ArgumentException("Use a general troubleshooting question without addresses, ticket numbers, URLs, credentials, or personal data.");
        await SupportRate("help-web:" + actor + ":" + DateTime.UtcNow.ToString("yyyy-MM-dd"), 20, 86400, ct);
        var url = status.Provider == "brave" ? "https://api.search.brave.com/res/v1/web/search?q=" + Uri.EscapeDataString(query) + "&count=10&extra_snippets=true&safesearch=strict" : configuration["WEB_GUIDANCE_SEARXNG_URL"]!.TrimEnd('/') + "/search?q=" + Uri.EscapeDataString(query) + "&format=json&safesearch=2&language=en-US";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("application/json");
            if (status.Provider == "brave")
                request.Headers.Add("X-Subscription-Token", configuration["BRAVE_SEARCH_API_KEY"]);
            using var response = await GuidanceClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = json.RootElement;
            var rows = status.Provider == "brave" && root.TryGetProperty("web", out var web) ? JArray(web, "results") : JArray(root, "results");
            var results = new List<WebGuidanceResult>();
            var seen = new HashSet<string>();
            string Clean(string value, int max)
            {
                var text = System.Net.WebUtility.HtmlDecode(Regex.Replace(value, "<[^>]*>", " "));
                text = Regex.Replace(text, @"\s+", " ").Trim();
                return text[..Math.Min(text.Length, max)];
            }
            foreach (var row in rows)
            {
                var uri = GuidanceUrl(JString(row, "url"));
                if (uri is null || !seen.Add(uri.AbsoluteUri))
                    continue;
                var title = Clean(JString(row, "title"), 180);
                if (title.Length == 0)
                    continue;
                var snippets = new[] { JString(row, status.Provider == "brave" ? "description" : "content") }.Concat(JArray(row, "extra_snippets").Where(x => x.ValueKind == JsonValueKind.String).Take(3).Select(x => x.GetString() ?? "")).Select(x => Clean(x, 300)).Where(x => x.Length > 0).ToArray();
                results.Add(new(title, uri.AbsoluteUri, uri.Host, snippets));
                if (results.Count == 10)
                    break;
            }
            return new
            {
                results,
                procedures = Array.Empty<object>(),
                note = "Search excerpts are shown with source links. Open the source for full instructions."
            };
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested) { throw new FeatureFailure(502, "WEB_SEARCH_UNAVAILABLE", "The configured documentation search provider is unavailable. Local Help search remains available."); }
    }
}
