using System.Text.Json;
using System.Text.RegularExpressions;

public static partial class FeatureApis
{
    private static void MapAdminEdits(RouteGroupBuilder api)
    {
        api.MapPatch("/admin/users/{id:guid}", async (string id, JsonElement b, HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.UpdateUserAccess(Actor(c), id, b, t)));
        api.MapPatch("/admin/{resource}/{id}", async (string resource, string id, JsonElement b, HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.UpdateDirectoryRecord(Actor(c), resource, id, b, t)));
        api.MapDelete("/admin/{resource}/{id}", async (string resource, string id, HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.DeleteDirectoryRecord(Actor(c), resource, id, await JsonSerializer.DeserializeAsync<JsonElement>(c.Request.Body, cancellationToken: t), t)));
    }
}
public sealed partial class MySqlStore
{
    private static (string Table, string Key, string[] Fields) DirectoryConfig(string resource) => resource switch
    {
        "clients" => ("clients", "id", ["name", "ticketPrefix", "primaryEmail", "primaryPhone", "notes"]),
        "contacts" => ("contacts", "id", ["clientId", "siteId", "name", "email", "phone"]),
        "sites" => ("sites", "id", ["clientId", "name", "address", "ticketPrefix"]),
        "assets" => ("assets", "id", ["clientId", "siteId", "name", "assetType", "serialNumber", "externalReference"]),
        "categories" => ("categories", "id", ["parentId", "name", "description"]),
        "tags" => ("tags", "id", ["name", "description"]),
        "sources" => ("ticket_sources", "code", ["name"]),
        "departments" => ("departments", "id", ["name", "description"]),
        _ => throw new KeyNotFoundException()
    };
    private static string Column(string field) => Regex.Replace(field, "[A-Z]", m => "_" + m.Value.ToLowerInvariant());
    private static void RecordProtection(string resource, string id)
    {
        if (resource == "clients" && id == "00000000-0000-4000-8000-000000000132")
            throw new InvalidOperationException("The internal organization is protected.");
    }
    public async Task<object> UpdateDirectoryRecord(string actor, string resource, string id, JsonElement body, CancellationToken ct)
    {
        await RequireAdmin(actor, ct);
        var config = DirectoryConfig(resource);
        RecordProtection(resource, id);
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("active", out var active) || active.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ArgumentException("Active must be true or false.");
        if (body.EnumerateObject().Any(p => p.Name != "active" && !config.Fields.Contains(p.Name)))
            throw new ArgumentException("The record contains unsupported fields.");
        var fields = new Dictionary<string, object?>();
        foreach (var field in config.Fields)
        {
            var value = JOptional(body, field);
            if (field == "name")
                value = FText(value, "Name", 2, 200);
            else if (field.EndsWith("Id") && value is not null)
                value = FGuid(value, field);
            else if (value is not null)
                value = FText(value, field, 0, field is "notes" or "description" ? 5000 : 500);
            fields[field] = value;
        }
        if (fields.TryGetValue("ticketPrefix", out var prefix) && prefix is string text && !Regex.IsMatch(text, "^[A-Z][A-Z0-9]{1,7}$"))
            throw new ArgumentException("Ticket prefix must be 2–8 uppercase letters or numbers, beginning with a letter.");
        foreach (var key in new[] { "email", "primaryEmail" })
        if (fields.GetValueOrDefault(key) is string mail && mail.Length > 0 && !System.Net.Mail.MailAddress.TryCreate(mail, out _))
            throw new ArgumentException("Enter a valid email address.");
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        if (resource == "categories")
            await FRows(connection, tx, "SELECT id FROM categories ORDER BY id FOR UPDATE", ct);
        var before = (await FRows(connection, tx, $"SELECT * FROM {config.Table} WHERE {config.Key}=@id FOR UPDATE", ct, ("id", id))).FirstOrDefault() ?? throw new KeyNotFoundException();
        if (resource is "contacts" or "sites" or "assets")
        {
            if (!Equals(fields["clientId"], before["client_id"]))
                throw new ArgumentException("Client ownership cannot be changed.");
            if (fields.GetValueOrDefault("siteId") is string site && (await FRows(connection, tx, "SELECT id FROM sites WHERE id=@site AND client_id=@client AND active=1 FOR SHARE", ct, ("site", site), ("client", before["client_id"]))).Count == 0)
                throw new ArgumentException("Site must belong to the selected client.");
        }
        if (resource == "categories" && fields["parentId"] is string parent)
        {
            if ((await FRows(connection, tx, "SELECT id FROM categories WHERE id=@parent AND active=1", ct, ("parent", parent))).Count == 0)
                throw new ArgumentException("Choose an active parent category.");
            if ((await FRows(connection, tx, "WITH RECURSIVE ancestors AS (SELECT id,parent_id FROM categories WHERE id=@parent UNION DISTINCT SELECT c.id,c.parent_id FROM categories c JOIN ancestors a ON c.id=a.parent_id) SELECT id FROM ancestors WHERE id=@id", ct, ("parent", parent), ("id", id))).Count > 0)
                throw new ArgumentException("Category hierarchy cannot contain a cycle.");
        }
        if (fields.ContainsKey("ticketPrefix") && fields["ticketPrefix"] is null)
            fields["ticketPrefix"] = before["ticket_prefix"];
        var args = fields.Select(p => (p.Key, p.Value)).Append(("id", (object?)id)).Append(("active", resource == "clients" ? (object)(active.GetBoolean() ? "ACTIVE" : "INACTIVE") : active.GetBoolean())).ToArray();
        await FExecute(connection, tx, $"UPDATE {config.Table} SET {string.Join(',', config.Fields.Select(f => Column(f) + "=@" + f))},{(resource == "clients" ? "status" : "active")}=@active,updated_at=UTC_TIMESTAMP(6) WHERE {config.Key}=@id", ct, args);
        await AddAudit(connection, tx, actor, "DIRECTORY.UPDATED", resource.ToUpperInvariant(), id, new
        {
            fields = config.Fields,
            active = active.GetBoolean()
        }, ct);
        await tx.CommitAsync(ct);
        return new
        {
            id
        };
    }
    public async Task<object> DeleteDirectoryRecord(string actor, string resource, string id, JsonElement body, CancellationToken ct)
    {
        await RequireAdmin(actor, ct);
        if (FText(JString(body, "confirmation"), "Confirmation", 1, 20).ToLowerInvariant() != "delete")
            throw new ArgumentException("Type delete to confirm removal.");
        RecordProtection(resource, id);
        var config = resource == "users" ? (Table: "users", Key: "id", Fields: Array.Empty<string>()) : DirectoryConfig(resource);
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        if (resource == "users")
        {
            await FRows(connection, tx, "SELECT id FROM users ORDER BY id FOR UPDATE", ct);
            if (id == actor)
                throw new InvalidOperationException("Administrators cannot remove their own account.");
            await EnsureOtherAdministrator(connection, tx, id, ct);
        }
        if ((await FRows(connection, tx, $"SELECT {config.Key} FROM {config.Table} WHERE {config.Key}=@id FOR UPDATE", ct, ("id", id))).Count == 0)
            throw new KeyNotFoundException();
        if (resource == "contacts" && (await FRows(connection, tx, "SELECT id FROM tickets WHERE requester_id=@id UNION ALL SELECT id FROM change_stakeholders WHERE contact_id=@id LIMIT 1", ct, ("id", id))).Count > 0)
        {
            await FExecute(connection, tx, "UPDATE contacts SET active=0,updated_at=UTC_TIMESTAMP(6) WHERE id=@id", ct, ("id", id));
            await AddAudit(connection, tx, actor, "CONTACT.DEACTIVATED", "CONTACT", id, new
            {
                reason = "Retained ticket or change history"
            }, ct);
            await tx.CommitAsync(ct);
            return new
            {
                id,
                deleted = false,
                deactivated = true
            };
        }
        (string Table, string Column)[] dependencies = resource switch
        {
            "clients" => [("contacts", "client_id"), ("sites", "client_id"), ("assets", "client_id"), ("client_users", "client_id"), ("tickets", "client_id"), ("changes", "client_id"), ("problems", "client_id")],
            "sites" => [("assets", "site_id"), ("contacts", "site_id"), ("tickets", "site_id"), ("changes", "site_id"), ("problems", "site_id")],
            "categories" => [("categories", "parent_id"), ("tickets", "category_id")],
            "sources" => [("tickets", "source")],
            _ => []
        };
        foreach (var dependency in dependencies)
        if ((await FRows(connection, tx, $"SELECT 1 FROM {dependency.Table} WHERE {dependency.Column}=@id LIMIT 1", ct, ("id", id))).Count > 0)
            throw new InvalidOperationException("This record has linked records. Deactivate it to retain its history.");
        await FExecute(connection, tx, $"DELETE FROM {config.Table} WHERE {config.Key}=@id", ct, ("id", id));
        await AddAudit(connection, tx, actor, "DIRECTORY.DELETED", resource.ToUpperInvariant(), id, new
        {
        }, ct);
        await tx.CommitAsync(ct);
        return new
        {
            id,
            deleted = true
        };
    }
    private static async Task EnsureOtherAdministrator(MySqlConnector.MySqlConnection connection, MySqlConnector.MySqlTransaction tx, string id, CancellationToken ct)
    {
        if ((await FRows(connection, tx, "SELECT u.id FROM users u JOIN user_roles r ON r.user_id=u.id WHERE u.active=1 AND r.role_code='ADMIN' AND u.id<>@id LIMIT 1", ct, ("id", id))).Count == 0)
            throw new InvalidOperationException("At least one active administrator must remain.");
    }
    public async Task<object> UpdateUserAccess(string actor, string id, JsonElement body, CancellationToken ct, DirectoryIdentity? provision = null)
    {
        await RequireAdmin(actor, ct);
        var roles = JArray(body, "roles").Select(x => x.GetString() ?? "").ToArray();
        if (roles.Length != 1 || !new[] { "ADMIN", "MANAGER", "TEAM_LEAD", "TECHNICIAN", "INTERN", "USER" }.Contains(roles[0]))
            throw new ArgumentException("Choose one valid role.");
        var role = roles[0];
        string[] Ids(string key) => JArray(body, key).Select(x => FGuid(x.GetString(), key)).Distinct().ToArray();
        var clients = Ids("clientIds").Where(x => x != "00000000-0000-4000-8000-000000000132").ToList();
        var sites = Ids("siteIds");
        var departments = Ids("departmentIds");
        var leads = Ids("leadDepartmentIds");
        var scope = role == "ADMIN" ? "ALL" : JOptional(body, "workScope") ?? "CLIENT";
        if (!new[] { "INTERNAL", "CLIENT", "BOTH", "ALL" }.Contains(scope))
            throw new ArgumentException("Choose a valid work scope.");
        if (role != "ADMIN" && scope == "ALL")
            throw new ArgumentException("All work scope is reserved for administrators.");
        var change = role == "ADMIN" ? "CHANGE_MANAGER" : JOptional(body, "changeAccess") ?? "NONE";
        if (!new[] { "NONE", "CONTRIBUTOR", "COORDINATOR", "APPROVER", "CHANGE_MANAGER" }.Contains(change))
            throw new ArgumentException("Choose valid change access.");
        if ((role == "USER" || scope is "CLIENT" or "BOTH") && clients.Count == 0)
            throw new ArgumentException("Select at least one client.");
        var supervisor = JOptional(body, "supervisorUserId");
        if (supervisor is not null)
            supervisor = FGuid(supervisor, "supervisor");
        if (role == "INTERN" && (scope is not ("INTERNAL" or "BOTH") || supervisor is null))
            throw new ArgumentException("Interns require an internal work scope and supervisor.");
        if (supervisor == id)
            throw new ArgumentException("An account cannot supervise itself.");
        var active = JBool(body, "active", true);
        var requested = JArray(body, "permissions").Select(x => x.GetString() ?? "").ToArray();
        if (requested.Any(x => !StaffPermissions.Contains(x) && !new[] { "AUDIT_VIEW", "USERS_MANAGE", "CLIENTS_MANAGE", "CONTACTS_MANAGE", "SITES_MANAGE" }.Contains(x)))
            throw new ArgumentException("Invalid permission.");
        var permissions = role == "ADMIN" ? Array.Empty<string>() : role == "MANAGER" ? StaffPermissions.Append("AUDIT_VIEW").ToArray() : role == "TEAM_LEAD" ? new[] { "TICKETS_TEAM_VIEW", "TICKETS_ASSIGN", "CATEGORY_MANAGE", "TAGS_MANAGE", "REPORTS_VIEW" } : role == "TECHNICIAN" ? new[] { "TICKETS_TEAM_VIEW", "TICKETS_ASSIGN" } : Array.Empty<string>();
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await FRows(connection, tx, "SELECT id FROM users ORDER BY id FOR UPDATE", ct);
        if (provision is not null)
            await FExecute(connection, tx, "INSERT INTO users(id,entra_object_id,entra_tenant_id,email,display_name) VALUES(@id,@object,@tenant,@email,@name)", ct, ("id", id), ("object", provision.Id), ("tenant", provision.TenantId), ("email", provision.Email), ("name", provision.DisplayName));
        if ((await FRows(connection, tx, "SELECT id FROM users WHERE id=@id", ct, ("id", id))).Count == 0)
            throw new KeyNotFoundException();
        if ((await FRows(connection, tx, "SELECT u.id FROM users u JOIN user_roles r ON r.user_id=u.id WHERE u.id=@actor AND u.active=1 AND r.role_code='ADMIN'", ct, ("actor", actor))).Count == 0)
            throw new UnauthorizedAccessException();
        if (!active || role != "ADMIN")
            await EnsureOtherAdministrator(connection, tx, id, ct);
        if (scope is "INTERNAL" or "BOTH" or "ALL")
            clients.Add("00000000-0000-4000-8000-000000000132");
        foreach (var client in clients)
        if ((await FRows(connection, tx, "SELECT id FROM clients WHERE id=@id AND status='ACTIVE'", ct, ("id", client))).Count == 0)
            throw new ArgumentException("Select active clients.");
        foreach (var site in sites)
        if (!(await FRows(connection, tx, "SELECT client_id FROM sites WHERE id=@id AND active=1", ct, ("id", site))).Any(x => clients.Contains((string)x["client_id"]!)))
            throw new ArgumentException("Sites must belong to assigned clients.");
        if (leads.Except(departments).Any())
            throw new ArgumentException("Lead departments must be assigned departments.");
        foreach (var department in departments)
        if ((await FRows(connection, tx, "SELECT id FROM departments WHERE id=@id AND active=1", ct, ("id", department))).Count == 0)
            throw new ArgumentException("Select active departments.");
        if (supervisor is not null && (await FRows(connection, tx, "SELECT u.id FROM users u JOIN user_roles r ON r.user_id=u.id WHERE u.id=@id AND u.active=1 AND r.role_code IN ('ADMIN','MANAGER','TEAM_LEAD','TECHNICIAN')", ct, ("id", supervisor))).Count == 0)
            throw new ArgumentException("Select an active staff supervisor.");
        await FExecute(connection, tx, "UPDATE users SET active=@active,work_scope=@scope,change_access=@change,supervisor_user_id=@supervisor,access_expires_at=NULL,session_version=session_version+1,updated_at=UTC_TIMESTAMP(6) WHERE id=@id", ct, ("active", active), ("scope", scope), ("change", change), ("supervisor", supervisor), ("id", id));
        foreach (var table in new[] { "user_roles", "client_users", "user_sites", "user_permissions", "department_users" })
            await FExecute(connection, tx, $"DELETE FROM {table} WHERE user_id=@id", ct, ("id", id));
        await FExecute(connection, tx, "INSERT INTO user_roles(user_id,role_code,granted_by) VALUES(@id,@role,@actor)", ct, ("id", id), ("role", role), ("actor", actor));
        foreach (var client in clients)
            await FExecute(connection, tx, "INSERT INTO client_users(user_id,client_id) VALUES(@id,@client)", ct, ("id", id), ("client", client));
        foreach (var site in sites)
            await FExecute(connection, tx, "INSERT INTO user_sites(user_id,site_id) VALUES(@id,@site)", ct, ("id", id), ("site", site));
        foreach (var permission in permissions)
            await FExecute(connection, tx, "INSERT INTO user_permissions(user_id,permission_code,granted_by) VALUES(@id,@permission,@actor)", ct, ("id", id), ("permission", permission), ("actor", actor));
        foreach (var department in departments)
            await FExecute(connection, tx, "INSERT INTO department_users(department_id,user_id,expertise,is_lead) VALUES(@department,@id,@expertise,@lead)", ct, ("department", department), ("id", id), ("expertise", JOptional(body, "expertise")), ("lead", leads.Contains(department)));
        await AddAudit(connection, tx, actor, "USER.ACCESS_UPDATED", "USER", id, new
        {
            roles,
            clientIds = clients,
            siteIds = sites,
            permissions,
            active,
            workScope = scope,
            changeAccess = change
        }, ct);
        await tx.CommitAsync(ct);
        return new
        {
            id
        };
    }
}
