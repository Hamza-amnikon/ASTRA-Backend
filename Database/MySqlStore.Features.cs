using System.Text.Json;
using MySqlConnector;

public sealed partial class MySqlStore
{
    private static MySqlCommand FCommand(MySqlConnection connection, MySqlTransaction? transaction, string sql, params (string Name, object? Value)[] args)
    {
        var command = new MySqlCommand(sql, connection, transaction);
        foreach (var (name, value) in args)
            command.Parameters.AddWithValue("@" + name, value ?? DBNull.Value);
        return command;
    }
    private static async Task<List<Dictionary<string, object?>>> FRows(MySqlConnection connection, MySqlTransaction? transaction, string sql, CancellationToken ct, params (string Name, object? Value)[] args)
    {
        await using var command = FCommand(connection, transaction, sql, args);
        return await ExecuteRows(command, ct);
    }
    private async Task<List<Dictionary<string, object?>>> FQuery(string sql, CancellationToken ct, params (string Name, object? Value)[] args)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        return await FRows(connection, null, sql, ct, args);
    }
    private static async Task<int> FExecute(MySqlConnection connection, MySqlTransaction transaction, string sql, CancellationToken ct, params (string Name, object? Value)[] args)
    {
        await using var command = FCommand(connection, transaction, sql, args);
        return await command.ExecuteNonQueryAsync(ct);
    }
    private async Task RequireAdmin(string actor, CancellationToken ct)
    {
        if (!await IsAdministrator(actor, ct))
            throw new UnauthorizedAccessException();
    }
    private static string FText(string? value, string label, int min = 1, int max = 10000)
    {
        value = value?.Trim() ?? "";
        if (value.Length < min || value.Length > max)
            throw new ArgumentException($"{label} must contain between {min} and {max} characters.");
        return value;
    }
    private static string FGuid(string? value, string label)
    {
        if (!Guid.TryParse(value, out var id))
            throw new ArgumentException($"Select a valid {label}.");
        return id.ToString("D");
    }
    private static string[] FStrings(object? value) => value is JsonElement json ? json.Deserialize<string[]>() ?? [] : value is string text ? JsonSerializer.Deserialize<string[]>(text) ?? [] : [];
    private static bool FBool(object? value) => value is not null && Convert.ToBoolean(value);
    private static int FInt(object? value) => value is null ? 0 : Convert.ToInt32(value);
    private static string FJson(object value) => JsonSerializer.Serialize(value);

    public async Task<object> GroupOptions(string actor, CancellationToken ct)
    {
        await RequireAdmin(actor, ct);
        var clients = await FQuery("SELECT id,name FROM clients WHERE status='ACTIVE' AND organization_type='CLIENT' ORDER BY name", ct);
        var users = await FQuery("SELECT u.id,u.display_name AS name,u.email FROM users u WHERE active=1 AND EXISTS(SELECT 1 FROM user_roles r WHERE r.user_id=u.id AND r.role_code IN ('TECHNICIAN','TEAM_LEAD','MANAGER','ADMIN')) ORDER BY display_name", ct);
        var contacts = await FQuery("SELECT c.id,c.name,c.email,c.client_id,c.user_id FROM contacts c JOIN clients cl ON cl.id=c.client_id WHERE c.active=1 AND cl.status='ACTIVE' AND cl.organization_type='CLIENT' ORDER BY c.name", ct);
        var permissions = await FQuery("SELECT code,name,description FROM permissions WHERE code IN ('TICKETS_TEAM_VIEW','TICKETS_ASSIGN','TICKETS_APPROVE','CATEGORY_MANAGE','SLA_MANAGE','REPORTS_VIEW','TAGS_MANAGE') ORDER BY name", ct);
        return new
        {
            clients,
            users,
            contacts,
            staffPermissions = permissions,
            customerPermissions = new[] {
            new { code="PORTAL_ORGANIZATION_VIEW", name="View organization tickets", description="Read tickets and public attachments for this customer organization." },
            new { code="PORTAL_ORGANIZATION_REPLY", name="Reply to organization tickets", description="Reply to organization tickets. Requires organization view permission." } }
        };
    }
    public async Task<object> Groups(string actor, string? search, string? kind, string? pageValue, CancellationToken ct)
    {
        await RequireAdmin(actor, ct);
        kind = string.IsNullOrEmpty(kind) ? "ALL" : kind;
        if (kind is not ("ALL" or "CUSTOMER" or "STAFF"))
            throw new ArgumentException("Select a valid group type.");
        search = FText(search, "Search", 0, 120);
        var page = int.TryParse(pageValue, out var p) ? Math.Clamp(p, 1, 100000) : 1;
        const string filter = "FROM access_groups g LEFT JOIN clients c ON c.id=g.client_id WHERE (@kind='ALL' OR g.kind=@kind) AND (@search='' OR LOWER(g.name) LIKE @pattern OR LOWER(c.name) LIKE @pattern)";
        var args = new (string, object?)[] { ("kind", kind), ("search", search), ("pattern", "%" + search.ToLowerInvariant().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%"), ("skip", (page - 1) * 20) };
        var count = await FQuery("SELECT COUNT(*) AS total " + filter, ct, args);
        var items = await FQuery("SELECT g.*,c.name AS client_name,CASE WHEN g.kind='STAFF' THEN (SELECT COUNT(*) FROM staff_group_members m WHERE m.group_id=g.id) ELSE (SELECT COUNT(*) FROM customer_group_members m WHERE m.group_id=g.id) END AS member_count " + filter + " ORDER BY LOWER(g.name),g.id LIMIT 20 OFFSET @skip", ct, args);
        return new
        {
            total = count[0]["total"],
            items,
            page,
            pageSize = 20
        };
    }
    public async Task<Dictionary<string, object?>> Group(string actor, string id, CancellationToken ct)
    {
        await RequireAdmin(actor, ct);
        var rows = await FQuery("SELECT * FROM access_groups WHERE id=@id", ct, ("id", id));
        var row = rows.FirstOrDefault() ?? throw new KeyNotFoundException();
        row["staff_members"] = (await FQuery("SELECT user_id AS id FROM staff_group_members WHERE group_id=@id", ct, ("id", id))).Select(x => x["id"]).ToArray();
        row["customer_members"] = (await FQuery("SELECT contact_id AS id FROM customer_group_members WHERE group_id=@id", ct, ("id", id))).Select(x => x["id"]).ToArray();
        return row;
    }
    public async Task<Dictionary<string, object?>> SaveGroup(string actor, string? id, GroupInput body, CancellationToken ct)
    {
        await RequireAdmin(actor, ct);
        var name = FText(body.Name, "Group name", 2, 120);
        var description = FText(body.Description, "Description", 0, 500);
        var allowed = body.Kind == "STAFF" ? StaffPermissions : body.Kind == "CUSTOMER" ? new[] { "PORTAL_ORGANIZATION_VIEW", "PORTAL_ORGANIZATION_REPLY" } : throw new ArgumentException("Choose a valid group type.");
        if (body.Permissions is null || body.MemberIds is null || body.Permissions.Length > 7 || body.Permissions.Distinct().Count() != body.Permissions.Length || body.Permissions.Any(x => !allowed.Contains(x)) || body.MemberIds.Length > 200 || body.MemberIds.Distinct().Count() != body.MemberIds.Length)
            throw new ArgumentException("Choose valid, unique group permissions and members.");
        if (body.Permissions.Contains("PORTAL_ORGANIZATION_REPLY") && !body.Permissions.Contains("PORTAL_ORGANIZATION_VIEW"))
            throw new ArgumentException("Organization replies require organization view permission.");
        var client = body.Kind == "CUSTOMER" ? FGuid(body.ClientId, "customer organization") : null;
        if (body.Kind == "STAFF" && body.ClientId is not null)
            throw new ArgumentException("Staff groups cannot be assigned to a customer.");
        var members = body.MemberIds.Select(x => FGuid(x, "member")).ToArray();
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        Dictionary<string, object?>? before = null;
        if (id is not null)
        {
            before = (await FRows(connection, tx, "SELECT * FROM access_groups WHERE id=@id FOR UPDATE", ct, ("id", id))).FirstOrDefault() ?? throw new KeyNotFoundException();
            if (FInt(before["revision"]) != body.Revision)
                throw new InvalidOperationException("This group changed. Reload before saving.");
            if ((string)before["kind"]! != body.Kind || (string?)before["client_id"] != client)
                throw new ArgumentException("Group type and customer cannot be changed. Create another group.");
        }
        if (client is not null && (await FRows(connection, tx, "SELECT id FROM clients WHERE id=@client AND status='ACTIVE' AND organization_type='CLIENT' FOR SHARE", ct, ("client", client))).Count == 0)
            throw new ArgumentException("Choose an active customer organization.");
        foreach (var member in members)
        {
            var sql = body.Kind == "STAFF" ? "SELECT u.id FROM users u WHERE id=@member AND active=1 AND EXISTS(SELECT 1 FROM user_roles r WHERE r.user_id=u.id AND r.role_code IN ('TECHNICIAN','TEAM_LEAD','MANAGER','ADMIN')) FOR SHARE" : "SELECT id FROM contacts WHERE id=@member AND client_id=@client AND active=1 FOR SHARE";
            if ((await FRows(connection, tx, sql, ct, ("member", member), ("client", client))).Count != 1)
                throw new ArgumentException("Choose active members belonging to the selected group type and customer.");
        }
        var groupId = id ?? Guid.NewGuid().ToString("D");
        await FExecute(connection, tx, id is null ? "INSERT INTO access_groups(id,name,description,kind,client_id,permissions,active) VALUES(@id,@name,@description,@kind,@client,CAST(@permissions AS JSON),@active)" : "UPDATE access_groups SET name=@name,description=@description,permissions=CAST(@permissions AS JSON),active=@active,revision=revision+1,updated_at=UTC_TIMESTAMP(6) WHERE id=@id", ct, ("id", groupId), ("name", name), ("description", description), ("kind", body.Kind), ("client", client), ("permissions", FJson(body.Permissions)), ("active", body.Active));
        var table = body.Kind == "STAFF" ? "staff_group_members" : "customer_group_members";
        await FExecute(connection, tx, $"DELETE FROM {table} WHERE group_id=@id", ct, ("id", groupId));
        foreach (var member in members)
            await FExecute(connection, tx, body.Kind == "STAFF" ? "INSERT INTO staff_group_members(group_id,user_id) VALUES(@id,@member)" : "INSERT INTO customer_group_members(group_id,contact_id,client_id) VALUES(@id,@member,@client)", ct, ("id", groupId), ("member", member), ("client", client));
        await AddAudit(connection, tx, actor, id is null ? "GROUP.CREATED" : "GROUP.UPDATED", "ACCESS_GROUP", groupId, new
        {
            name,
            kind = body.Kind,
            clientId = client,
            before,
            after = new
            {
                permissions = body.Permissions,
                active = body.Active,
                memberIds = members
            }
        }, ct);
        var saved = (await FRows(connection, tx, "SELECT * FROM access_groups WHERE id=@id", ct, ("id", groupId)))[0];
        await tx.CommitAsync(ct);
        return saved;
    }
}
