using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Astra.Api.Data;
using Astra.Api.Security;

namespace Astra.Api.Services;

public sealed class AdministrationService(Database database)
{
    private static readonly IReadOnlyDictionary<string, (string Table, string Permission)> Resources = new Dictionary<string, (string, string)>(StringComparer.Ordinal)
    {
        ["clients"] = ("clients", "CLIENTS_MANAGE"),
        ["contacts"] = ("contacts", "CONTACTS_MANAGE"),
        ["sites"] = ("sites", "SITES_MANAGE"),
        ["assets"] = ("assets", ""),
        ["categories"] = ("categories", "CATEGORY_MANAGE"),
        ["tags"] = ("tags", "TAGS_MANAGE"),
        ["sources"] = ("ticket_sources", "SOURCES_MANAGE"),
        ["departments"] = ("departments", "USERS_MANAGE"),
        ["audit-logs"] = ("audit_logs", "AUDIT_VIEW"),
        ["users"] = ("users", "")
    };

    public static void Authorize(Actor actor, string resource)
    {
        if (!Resources.TryGetValue(resource, out var config)) throw new ApiFault(404, "Not found.");
        if (!actor.Roles.Contains("ADMIN") && (config.Permission.Length == 0 || !actor.Permissions.Contains(config.Permission))) throw new ApiFault(403, "Permission required.");
    }
    public async Task<object> OverviewAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        return (await Database.QueryAsync(connection, """
          SELECT (SELECT COUNT(*) FROM dbo.users) AS users,(SELECT COUNT(*) FROM dbo.tickets) AS tickets,
          (SELECT COUNT(*) FROM dbo.clients) AS clients,(SELECT COUNT(*) FROM dbo.sites) AS sites,
          (SELECT COUNT(*) FROM dbo.assets) AS assets,(SELECT COUNT(*) FROM dbo.users WHERE active=1) AS active_users,
          (SELECT COUNT(*) FROM dbo.clients WHERE organization_type='CLIENT' AND status='ACTIVE') AS active_customers,
          (SELECT COUNT(*) FROM dbo.contacts WHERE active=1) AS active_contacts,
          (SELECT COUNT(*) FROM dbo.access_groups WHERE active=1) AS active_groups,
          (SELECT COUNT(*) FROM dbo.access_groups WHERE active=1 AND kind='CUSTOMER') AS customer_groups,
          (SELECT COUNT(*) FROM dbo.access_groups WHERE active=1 AND kind='STAFF') AS staff_groups
          """, cancellationToken: cancellationToken)).Single();
    }

    public async Task<object> ListAsync(Actor actor, string resource, IQueryCollection query, CancellationToken cancellationToken)
    {
        Authorize(actor, resource);
        RequestQuery.Only(query, "q", "page", "pageSize", "status", "clientId");
        var q = RequestQuery.Text(query, "q"); var page = RequestQuery.Integer(query, "page", 1, 100000); var size = RequestQuery.Integer(query, "pageSize", 20, 100);
        var status = RequestQuery.Text(query, "status", "all"); if (status is not ("all" or "active" or "inactive")) throw new ApiFault(400, "Invalid status.");
        var client = RequestQuery.Id(query, "clientId");
        var table = Resources[resource].Table;
        string source, order = "r.name,r.id";
        if (resource == "users")
        {
            static string Array(string sql) => "JSON_QUERY(COALESCE((" + sql + " FOR JSON PATH),N'[]'))";
            source = "SELECT u.id,u.display_name AS name,u.email,u.active,u.last_login_at,u.work_scope,u.change_access,u.supervisor_user_id," +
              Array("SELECT role_code AS value FROM dbo.user_roles WHERE user_id=u.id ORDER BY role_code") + " AS roles," +
              Array("SELECT client_id AS value FROM dbo.client_users WHERE user_id=u.id ORDER BY client_id") + " AS client_ids," +
              Array("SELECT site_id AS value FROM dbo.user_sites WHERE user_id=u.id ORDER BY site_id") + " AS site_ids," +
              Array("SELECT permission_code AS value FROM dbo.user_permissions WHERE user_id=u.id ORDER BY permission_code") + " AS permissions," +
              Array("SELECT department_id AS value FROM dbo.department_users WHERE user_id=u.id ORDER BY department_id") + " AS department_ids," +
              Array("SELECT department_id AS value FROM dbo.department_users WHERE user_id=u.id AND is_lead=1 ORDER BY department_id") + " AS lead_department_ids," +
              "(SELECT STRING_AGG(e.expertise,'; ') FROM(SELECT DISTINCT expertise FROM dbo.department_users WHERE user_id=u.id AND expertise IS NOT NULL)e) AS expertise FROM dbo.users u";
        }
        else if (resource == "audit-logs")
        {
            source = "SELECT a.id,a.action AS name,a.action,a.entity_type,a.entity_id,a.details,a.created_at,COALESCE(u.display_name,'System') AS actor_name FROM dbo.audit_logs a LEFT JOIN dbo.users u ON u.id=a.actor_user_id";
            order = "r.id DESC";
        }
        else
        {
            var joined = resource is "contacts" or "sites" or "assets";
            // Select captured application columns explicitly so internal computed
            // index keys never appear in UI search results or response records.
            var columns = ColumnCatalog.Columns(table).Select(c => "t.[" + c + "]");
            source = "SELECT " + string.Join(',', columns) + (resource == "sources" ? ",t.code AS id" : "") + (joined ? ",c.name AS client_name" : "") +
                (resource == "sites" && actor.Roles.Contains("ADMIN") ? ",c.ticket_prefix AS client_ticket_prefix,c.organization_type,c.status AS client_status,JSON_QUERY((SELECT d.id,d.domain,d.active FROM dbo.client_email_domains d WHERE d.site_id=t.id AND d.client_id=t.client_id ORDER BY d.domain FOR JSON PATH)) AS email_domains" : "") +
                " FROM dbo." + table + " t" + (joined ? " JOIN dbo.clients c ON c.id=t.client_id" : "");
        }
        var active = resource == "clients" ? "CASE WHEN r.status='ACTIVE' THEN 1 ELSE 0 END" : resource == "audit-logs" ? "1" : "r.active";
        var scoped = resource is "contacts" or "sites" or "assets" ? "r.client_id=@client" : resource == "users" ? "EXISTS(SELECT 1 FROM dbo.client_users cu WHERE cu.user_id=r.id AND cu.client_id=@client)" : "1=0";
        var where = $"(@q=N'' OR (SELECT r.* FOR JSON PATH,WITHOUT_ARRAY_WRAPPER,INCLUDE_NULL_VALUES) COLLATE Latin1_General_100_CI_AS_SC LIKE @pattern ESCAPE N'\\') AND (@status='all' OR {active}=CASE WHEN @status='active' THEN 1 ELSE 0 END) AND (@client IS NULL OR {scoped})";
        var parameters = new Dictionary<string, object?> { ["q"] = q, ["pattern"] = RequestQuery.Like(q), ["status"] = status, ["client"] = client, ["size"] = size, ["offset"] = (page - 1) * size };
        await using var connection = await database.OpenAsync(cancellationToken);
        var row = (await Database.QueryAsync(connection, $"WITH records AS({source}),filtered AS(SELECT r.* FROM records r WHERE {where}) SELECT (SELECT COUNT(*) FROM filtered) AS total,(SELECT r.* FROM filtered r ORDER BY {order} OFFSET @offset ROWS FETCH NEXT @size ROWS ONLY FOR JSON PATH,INCLUDE_NULL_VALUES) AS items", parameters, cancellationToken: cancellationToken)).Single();
        var items = JsonNode.Parse(row["items"] as string ?? "[]")!.AsArray();
        if (resource == "users") foreach (var item in items)
        {
            foreach (var field in new[] { "roles", "client_ids", "site_ids", "permissions", "department_ids", "lead_department_ids" })
            {
                var array = item![field];
                if (array is JsonValue value) array = JsonNode.Parse(value.GetValue<string>());
                array ??= new JsonArray();
                item[field] = new JsonArray(array.AsArray().Select(n => n!["value"]!.DeepClone()).ToArray());
            }
        }
        if (resource == "sites" && actor.Roles.Contains("ADMIN")) foreach (var item in items)
        {
            var domains = item!["email_domains"];
            item["email_domains"] = domains is JsonValue value ? JsonNode.Parse(value.GetValue<string>()) : domains?.DeepClone() ?? new JsonArray();
        }
        if (resource == "audit-logs" && items.Count > 0)
        {
            var references = await Database.QueryAsync(connection, """
            SELECT id,label FROM (SELECT id,name AS label FROM dbo.clients UNION ALL SELECT id,name FROM dbo.contacts
              UNION ALL SELECT id,name FROM dbo.sites UNION ALL SELECT id,name FROM dbo.assets
              UNION ALL SELECT c.id,CASE WHEN p.name IS NULL THEN c.name ELSE p.name+N' → '+c.name END FROM dbo.categories c LEFT JOIN dbo.categories p ON p.id=c.parent_id
              UNION ALL SELECT id,name FROM dbo.tags UNION ALL SELECT id,display_name FROM dbo.users
              UNION ALL SELECT id,ticket_number FROM dbo.tickets UNION ALL SELECT id,name FROM dbo.access_groups) reference_names
            """, cancellationToken: cancellationToken);
            var names = references.ToDictionary(r => (Guid)r["id"]!, r => (string)r["label"]!);
            foreach (var item in items)
            {
                item!["entity_name"] = Guid.TryParse(item["entity_id"]?.ToString(), out var id) ? names.GetValueOrDefault(id) : null;
                var details = item["details"] is JsonValue value && value.TryGetValue<string>(out var serialized) ? JsonNode.Parse(serialized) : item["details"];
                item["details"] = SanitizeAudit(details, names);
            }
        }
        return new { total = row["total"], items, page, pageSize = size };
    }

    private static readonly HashSet<string> PrivateAuditFields = new(["phone", "primary_phone", "email", "primary_email", "requester_email", "entra_object_id", "entra_tenant_id"], StringComparer.Ordinal);
    private static JsonNode? SanitizeAudit(JsonNode? node, IReadOnlyDictionary<Guid, string> references) => node switch
    {
        JsonObject obj => new JsonObject(obj.Where(p => !PrivateAuditFields.Contains(Regex.Replace(p.Key, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant())).Select(p => new KeyValuePair<string, JsonNode?>(p.Key, SanitizeAudit(p.Value, references))).ToArray()),
        JsonArray array => new JsonArray(array.Select(n => SanitizeAudit(n, references)).ToArray()),
        JsonValue value when value.TryGetValue<string>(out var text) && Regex.IsMatch(text, "^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", RegexOptions.IgnoreCase) => JsonValue.Create(references.GetValueOrDefault(Guid.Parse(text))),
        _ => node?.DeepClone()
    };


}
