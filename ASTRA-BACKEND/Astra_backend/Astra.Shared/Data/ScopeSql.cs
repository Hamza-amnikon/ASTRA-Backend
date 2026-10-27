using System.Text.Json;
using Astra.Shared.Security;

namespace Astra.Shared.Data;

public static class ScopeSql
{
    public static Dictionary<string, object?> Parameters(Actor actor) => new()
    {
        ["scopeAdmin"] = actor.Roles.Contains("ADMIN"),
        ["scopeClients"] = JsonSerializer.Serialize(actor.ClientIds),
        ["scopeSites"] = JsonSerializer.Serialize(actor.SiteIds),
        ["scopeSiteCount"] = actor.SiteIds.Count
    };

    public static string Records(string alias) => $"(@scopeAdmin=1 OR {alias}.client_id IN (SELECT `value` FROM JSON_TABLE(@scopeClients, '$[*]' COLUMNS(`value` VARCHAR(100) PATH '$')) AS json_values)) AND (@scopeAdmin=1 OR @scopeSiteCount=0 OR {alias}.site_id IN (SELECT `value` FROM JSON_TABLE(@scopeSites, '$[*]' COLUMNS(`value` VARCHAR(100) PATH '$')) AS json_values))";
}
