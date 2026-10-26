using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Api.Data;
using Astra.Api.Security;
using Microsoft.Data.SqlClient;

namespace Astra.Api.Services;

public sealed class GroupService(Database database)
{
    public static readonly string[] StaffPermissions = ["TICKETS_TEAM_VIEW", "TICKETS_ASSIGN", "TICKETS_APPROVE", "CATEGORY_MANAGE", "SLA_MANAGE", "REPORTS_VIEW", "TAGS_MANAGE"];
    public static readonly string[] CustomerPermissions = ["PORTAL_ORGANIZATION_VIEW", "PORTAL_ORGANIZATION_REPLY"];
    private static readonly string[] StaffRoles = ["TECHNICIAN", "TEAM_LEAD", "MANAGER", "ADMIN"];
    public static void Validate(GroupInput input)
    {
        if (input.Name is null || input.Name.Length > 120 || input.Name.Trim().Length < 2 || input.Description is null || input.Description.Length > 500 || input.Kind is not ("STAFF" or "CUSTOMER") || input.Permissions is null || input.MemberIds is null || input.Permissions.Length > 7 || input.MemberIds.Length > 200 || input.MemberIds.Distinct().Count() != input.MemberIds.Length || input.Permissions.Distinct(StringComparer.Ordinal).Count() != input.Permissions.Length || input.Revision is < 1) throw new ApiFault(400, "Invalid group details.");
        var allowed = input.Kind == "STAFF" ? StaffPermissions : CustomerPermissions;
        if (input.Permissions.Any(p => !allowed.Contains(p, StringComparer.Ordinal))) throw new ApiFault(400, "Choose permissions matching the group type.");
        if (input.Permissions.Contains("PORTAL_ORGANIZATION_REPLY") && !input.Permissions.Contains("PORTAL_ORGANIZATION_VIEW")) throw new ApiFault(400, "Organization replies require organization view permission.");
        if ((input.Kind == "CUSTOMER" && !input.ClientId.HasValue) || (input.Kind == "STAFF" && input.ClientId.HasValue)) throw new ApiFault(400, "Only customer groups require a customer organization.");
    }
    private static Dictionary<string, object?> Decode(Dictionary<string, object?> row)
    {
        row["permissions"] = JsonNode.Parse((string)row["permissions"]!);
        return row;
    }
    public async Task<object> OptionsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        var clients = await Database.QueryAsync(connection, "SELECT id,name FROM dbo.clients WHERE status='ACTIVE' AND organization_type='CLIENT' ORDER BY name", cancellationToken: cancellationToken);
        var users = await Database.QueryAsync(connection, "SELECT u.id,u.display_name AS name,u.email FROM dbo.users u WHERE u.active=1 AND EXISTS(SELECT 1 FROM dbo.user_roles ur WHERE ur.user_id=u.id AND ur.role_code IN ('TECHNICIAN','TEAM_LEAD','MANAGER','ADMIN')) ORDER BY u.display_name", cancellationToken: cancellationToken);
        var contacts = await Database.QueryAsync(connection, "SELECT c.id,c.name,c.email,c.client_id,c.user_id FROM dbo.contacts c JOIN dbo.clients cl ON cl.id=c.client_id WHERE c.active=1 AND cl.status='ACTIVE' AND cl.organization_type='CLIENT' ORDER BY c.name", cancellationToken: cancellationToken);
        var permissions = await Database.QueryAsync(connection, "SELECT code,name,description FROM dbo.permissions WHERE code IN (SELECT [value] FROM OPENJSON(@codes)) ORDER BY name", new Dictionary<string, object?> { ["codes"] = JsonSerializer.Serialize(StaffPermissions) }, cancellationToken: cancellationToken);
        return new
        {
            clients,
            users,
            contacts,
            staffPermissions = permissions,
            customerPermissions = new[]
        {
            new{code="PORTAL_ORGANIZATION_VIEW",name="View organization tickets",description="Read tickets and public attachments for the selected customer organization in the authenticated client workspace."},
            new{code="PORTAL_ORGANIZATION_REPLY",name="Reply to organization tickets",description="Add public replies and attachments to organization tickets. Requires view permission."}
        }
        };
    }
    public async Task<object> ListAsync(IQueryCollection query, CancellationToken cancellationToken)
    {
        RequestQuery.Only(query, "q", "kind", "page");
        var q = RequestQuery.Text(query, "q", maximum: 120); var kind = RequestQuery.Text(query, "kind", "ALL"); var page = RequestQuery.Integer(query, "page", 1, 100000);
        if (kind is not ("ALL" or "STAFF" or "CUSTOMER")) throw new ApiFault(400, "Invalid group type.");
        var parameters = new Dictionary<string, object?> { ["q"] = q, ["pattern"] = RequestQuery.Like(q), ["kind"] = kind, ["offset"] = (page - 1) * 20 };
        await using var connection = await database.OpenAsync(cancellationToken);
        var columns = string.Join(',', ColumnCatalog.Columns("access_groups").Select(c => "g.[" + c + "]"));
        var row = (await Database.QueryAsync(connection, $"""
            WITH visible AS(SELECT {columns},c.name AS client_name,
            CASE WHEN g.kind='STAFF' THEN (SELECT COUNT(*) FROM dbo.staff_group_members m WHERE m.group_id=g.id)
              ELSE (SELECT COUNT(*) FROM dbo.customer_group_members m WHERE m.group_id=g.id) END AS member_count
            FROM dbo.access_groups g LEFT JOIN dbo.clients c ON c.id=g.client_id
            WHERE (@kind='ALL' OR g.kind=@kind) AND (@q='' OR g.name COLLATE Latin1_General_100_CI_AS_SC LIKE @pattern ESCAPE N'\' OR c.name COLLATE Latin1_General_100_CI_AS_SC LIKE @pattern ESCAPE N'\'))
            SELECT (SELECT COUNT(*) FROM visible) AS total,(SELECT * FROM visible ORDER BY LOWER(name),id OFFSET @offset ROWS FETCH NEXT 20 ROWS ONLY FOR JSON PATH,INCLUDE_NULL_VALUES) AS items
            """, parameters, cancellationToken: cancellationToken)).Single();
        var items = JsonNode.Parse(row["items"] as string ?? "[]")!.AsArray();
        foreach (var item in items) item!["permissions"] = JsonNode.Parse(item["permissions"]!.GetValue<string>());
        return new { total = row["total"], items, page, pageSize = 20 };
    }
    public async Task<object> DetailAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken); var parameters = new Dictionary<string, object?> { ["id"] = id };
        var rows = await Database.QueryAsync(connection, "SELECT " + string.Join(',', ColumnCatalog.Columns("access_groups").Select(c => "[" + c + "]")) + " FROM dbo.access_groups WHERE id=@id", parameters, cancellationToken: cancellationToken);
        if (rows.Count == 0) throw new ApiFault(404, "Group not found.");
        var row = Decode(rows[0]);
        row["staff_members"] = (await Database.QueryAsync(connection, "SELECT user_id AS id FROM dbo.staff_group_members WHERE group_id=@id", parameters, cancellationToken: cancellationToken)).Select(r => r["id"]).ToArray();
        row["customer_members"] = (await Database.QueryAsync(connection, "SELECT contact_id AS id FROM dbo.customer_group_members WHERE group_id=@id", parameters, cancellationToken: cancellationToken)).Select(r => r["id"]).ToArray();
        return row;
    }
    public async Task<object> SaveAsync(Actor actor, GroupInput input, Guid? id, CancellationToken cancellationToken)
    {
        Validate(input);
        await using var connection = await database.OpenAsync(cancellationToken); await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var parameters = new Dictionary<string, object?> { ["id"] = id, ["name"] = input.Name.Trim(), ["description"] = input.Description.Trim(), ["kind"] = input.Kind, ["client"] = input.ClientId, ["permissions"] = JsonSerializer.Serialize(input.Permissions), ["active"] = input.Active, ["members"] = JsonSerializer.Serialize(input.MemberIds) };
        var prior = id.HasValue ? await Database.QueryAsync(connection, "SELECT id,kind,client_id,permissions,active,revision FROM dbo.access_groups WITH (UPDLOCK,HOLDLOCK) WHERE id=@id", parameters, transaction, cancellationToken) : [];
        if (id.HasValue && prior.Count == 0) throw new ApiFault(404, "Group not found.");
        var before = prior.Count > 0 ? prior[0] : null;
        if (before is not null)
        {
            if ((string)before["kind"]! != input.Kind || (before["client_id"] as Guid?) != input.ClientId) throw new ApiFault(400, "Group type and customer cannot be changed. Create another group.");
            if (Convert.ToInt32(before["revision"]) != input.Revision) throw new ApiFault(400, "This group changed. Reload it before saving.");
        }
        if (input.Kind == "CUSTOMER" && (await Database.QueryAsync(connection, "SELECT id FROM dbo.clients WITH(HOLDLOCK) WHERE id=@client AND status='ACTIVE' AND organization_type='CLIENT'", parameters, transaction, cancellationToken)).Count == 0) throw new ApiFault(400, "Choose an active customer organization.");
        if (input.MemberIds.Length > 0)
        {
            var members = await Database.QueryAsync(connection, input.Kind == "CUSTOMER" ?
                "SELECT id FROM dbo.contacts WITH(HOLDLOCK) WHERE id IN(SELECT CONVERT(uniqueidentifier,[value]) FROM OPENJSON(@members)) AND client_id=@client AND active=1" :
                "SELECT u.id FROM dbo.users u WITH(HOLDLOCK) WHERE u.id IN(SELECT CONVERT(uniqueidentifier,[value]) FROM OPENJSON(@members)) AND u.active=1 AND EXISTS(SELECT 1 FROM dbo.user_roles ur WHERE ur.user_id=u.id AND ur.role_code IN('TECHNICIAN','TEAM_LEAD','MANAGER','ADMIN'))", parameters, transaction, cancellationToken);
            if (members.Count != input.MemberIds.Length) throw new ApiFault(400, input.Kind == "CUSTOMER" ? "Choose active contacts from this customer." : "Choose active staff accounts with staff roles.");
        }
        parameters["id"] ??= Guid.NewGuid();
        var memberTable = input.Kind == "STAFF" ? "staff_group_members" : "customer_group_members"; var memberColumn = input.Kind == "STAFF" ? "user_id" : "contact_id";
        var priorMembers = (await Database.QueryAsync(connection, $"SELECT {memberColumn} AS id FROM dbo.{memberTable} WHERE group_id=@id", parameters, transaction, cancellationToken)).Select(r => r["id"]).ToArray();
        await Database.ExecuteAsync(connection, id.HasValue ?
            "UPDATE dbo.access_groups SET name=@name,description=@description,permissions=@permissions,active=@active,revision=revision+1,updated_at=SYSUTCDATETIME() WHERE id=@id" :
            "INSERT dbo.access_groups(id,name,description,kind,client_id,permissions,active) VALUES(@id,@name,@description,@kind,@client,@permissions,@active)", parameters, transaction, cancellationToken);
        await Database.ExecuteAsync(connection, $"DELETE dbo.{memberTable} WHERE group_id=@id", parameters, transaction, cancellationToken);
        await Database.ExecuteAsync(connection, input.Kind == "STAFF" ? "INSERT dbo.staff_group_members(group_id,user_id) SELECT @id,CONVERT(uniqueidentifier,[value]) FROM OPENJSON(@members)" : "INSERT dbo.customer_group_members(group_id,contact_id,client_id) SELECT @id,CONVERT(uniqueidentifier,[value]),@client FROM OPENJSON(@members)", parameters, transaction, cancellationToken);
        await Audit.WriteAsync(connection, transaction, actor.Id, id.HasValue ? "GROUP.UPDATED" : "GROUP.CREATED", "ACCESS_GROUP", (Guid)parameters["id"]!, new { name = input.Name.Trim(), kind = input.Kind, clientId = input.ClientId, before = before is null ? null : new { permissions = JsonNode.Parse((string)before["permissions"]!), active = before["active"], memberIds = priorMembers }, after = new { input.Permissions, input.Active, MemberIds = input.MemberIds } }, cancellationToken);
        var rows = await Database.QueryAsync(connection, "SELECT " + string.Join(',', ColumnCatalog.Columns("access_groups").Select(c => "[" + c + "]")) + " FROM dbo.access_groups WHERE id=@id", parameters, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken); return Decode(rows.Single());
    }
    private static async Task<Actor> Administrator(HttpContext context, AuthService auth)
    { var actor = await auth.CurrentAsync(context); if (!actor.Roles.Contains("ADMIN")) throw new ApiFault(403, "Administration access required."); return actor; }

}
