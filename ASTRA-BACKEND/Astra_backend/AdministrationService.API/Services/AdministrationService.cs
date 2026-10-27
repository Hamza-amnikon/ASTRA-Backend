using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Astra.Shared.Data;
using Astra.Shared.Security;

namespace AdministrationService.API.Services;

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
          SELECT (SELECT COUNT(*) FROM users) AS users,(SELECT COUNT(*) FROM tickets) AS tickets,
          (SELECT COUNT(*) FROM clients) AS clients,(SELECT COUNT(*) FROM sites) AS sites,
          (SELECT COUNT(*) FROM assets) AS assets,(SELECT COUNT(*) FROM users WHERE active=1) AS active_users,
          (SELECT COUNT(*) FROM clients WHERE organization_type='CLIENT' AND status='ACTIVE') AS active_customers,
          (SELECT COUNT(*) FROM contacts WHERE active=1) AS active_contacts,
          (SELECT COUNT(*) FROM access_groups WHERE active=1) AS active_groups,
          (SELECT COUNT(*) FROM access_groups WHERE active=1 AND kind='CUSTOMER') AS customer_groups,
          (SELECT COUNT(*) FROM access_groups WHERE active=1 AND kind='STAFF') AS staff_groups
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
            static string Array(string sql)
            {
                var match = Regex.Match(sql, @"^SELECT (.+) AS value FROM (.+?)(?: ORDER BY .+)?$");
                return "(SELECT COALESCE(JSON_ARRAYAGG(JSON_OBJECT('value'," + match.Groups[1].Value + ")),JSON_ARRAY()) FROM " + match.Groups[2].Value + ")";
            }
            source = "SELECT u.id,u.display_name AS name,u.email,u.active,u.last_login_at,u.work_scope,u.change_access,u.supervisor_user_id," +
              Array("SELECT role_code AS value FROM user_roles WHERE user_id=u.id ORDER BY role_code") + " AS roles," +
              Array("SELECT client_id AS value FROM client_users WHERE user_id=u.id ORDER BY client_id") + " AS client_ids," +
              Array("SELECT site_id AS value FROM user_sites WHERE user_id=u.id ORDER BY site_id") + " AS site_ids," +
              Array("SELECT permission_code AS value FROM user_permissions WHERE user_id=u.id ORDER BY permission_code") + " AS permissions," +
              Array("SELECT department_id AS value FROM department_users WHERE user_id=u.id ORDER BY department_id") + " AS department_ids," +
              Array("SELECT department_id AS value FROM department_users WHERE user_id=u.id AND is_lead=1 ORDER BY department_id") + " AS lead_department_ids," +
              "(SELECT GROUP_CONCAT(DISTINCT expertise ORDER BY expertise SEPARATOR '; ') FROM department_users WHERE user_id=u.id AND expertise IS NOT NULL) AS expertise FROM users u";
        }
        else if (resource == "audit-logs")
        {
            source = "SELECT a.id,a.action AS name,a.action,a.entity_type,a.entity_id,a.details,a.created_at,COALESCE(u.display_name,'System') AS actor_name FROM audit_logs a LEFT JOIN users u ON u.id=a.actor_user_id";
            order = "r.id DESC";
        }
        else
        {
            var joined = resource is "contacts" or "sites" or "assets";
            // Select captured application columns explicitly so internal computed
            // index keys never appear in UI search results or response records.
            var columns = ColumnCatalog.Columns(table).Select(c => "t.`" + c + "`");
            source = "SELECT " + string.Join(',', columns) + (resource == "sources" ? ",t.code AS id" : "") + (joined ? ",c.name AS client_name" : "") +
                (resource == "sites" && actor.Roles.Contains("ADMIN") ? ",c.ticket_prefix AS client_ticket_prefix,c.organization_type,c.status AS client_status,(SELECT COALESCE(JSON_ARRAYAGG(JSON_OBJECT('id',d.id,'domain',d.domain,'active',IF(d.active,CAST('true' AS JSON),CAST('false' AS JSON)))),JSON_ARRAY()) FROM client_email_domains d WHERE d.site_id=t.id AND d.client_id=t.client_id) AS email_domains" : "") +
                " FROM " + table + " t" + (joined ? " JOIN clients c ON c.id=t.client_id" : "");
        }
        var active = resource == "clients" ? "CASE WHEN r.status='ACTIVE' THEN 1 ELSE 0 END" : resource == "audit-logs" ? "1" : "r.active";
        var scoped = resource is "contacts" or "sites" or "assets" ? "r.client_id=@client" : resource == "users" ? "EXISTS(SELECT 1 FROM client_users cu WHERE cu.user_id=r.id AND cu.client_id=@client)" : "1=0";
        var searchFields = resource == "users" ? new[] { "id", "name", "email", "work_scope", "change_access", "expertise" }
            : resource == "audit-logs" ? new[] { "id", "name", "action", "entity_type", "entity_id", "actor_name", "details" }
            : ColumnCatalog.Columns(table).ToArray();
        var search = "CONCAT_WS(' '," + string.Join(',', searchFields.Select(field => "CAST(r.`" + field + "` AS CHAR)")) + ")";
        var where = $"(@q='' OR {search} COLLATE utf8mb4_0900_ai_ci LIKE @pattern ESCAPE '~') AND (@status='all' OR {active}=CASE WHEN @status='active' THEN 1 ELSE 0 END) AND (@client IS NULL OR {scoped})";
        var parameters = new Dictionary<string, object?> { ["q"] = q, ["pattern"] = RequestQuery.Like(q), ["status"] = status, ["client"] = client, ["size"] = size, ["offset"] = (page - 1) * size };
        await using var connection = await database.OpenAsync(cancellationToken);
        var row = (await Database.QueryAsync(connection, $"SELECT COUNT(*) AS total FROM ({source}) r WHERE {where}", parameters, cancellationToken: cancellationToken)).Single();
        var records = await Database.QueryAsync(connection, $"SELECT r.* FROM ({source}) r WHERE {where} ORDER BY {order} LIMIT @size OFFSET @offset", parameters, cancellationToken: cancellationToken);
        var items = System.Text.Json.JsonSerializer.SerializeToNode(records, System.Text.Json.JsonSerializerOptions.Web)!.AsArray();
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
            SELECT 'CLIENT' AS entity_type,id,name AS label FROM clients
              UNION ALL SELECT 'CONTACT',id,name FROM contacts
              UNION ALL SELECT 'SITE',id,name FROM sites UNION ALL SELECT 'ASSET',id,name FROM assets
              UNION ALL SELECT 'CATEGORY',c.id,CASE WHEN p.name IS NULL THEN c.name ELSE CONCAT(p.name,' → ',c.name) END FROM categories c LEFT JOIN categories p ON p.id=c.parent_id
              UNION ALL SELECT 'TAG',id,name FROM tags UNION ALL SELECT 'USER',id,display_name FROM users
              UNION ALL SELECT 'TICKET',id,ticket_number FROM tickets UNION ALL SELECT 'ACCESS_GROUP',id,name FROM access_groups
            """, cancellationToken: cancellationToken);
            var entityNames = references.ToDictionary(r => ((string)r["entity_type"]!, (Guid)r["id"]!), r => (string)r["label"]!);
            var names = references.GroupBy(r => (Guid)r["id"]!).Where(group => group.Select(row => (string)row["label"]!).Distinct().Count() == 1)
                .ToDictionary(group => group.Key, group => (string)group.First()["label"]!);
            foreach (var item in items)
            {
                item!["entity_name"] = Guid.TryParse(item["entity_id"]?.ToString(), out var id) ? entityNames.GetValueOrDefault((item["entity_type"]!.GetValue<string>(), id)) : null;
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
