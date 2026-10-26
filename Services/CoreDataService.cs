using System.Data;
using System.Net.Mail;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Astra.Api.Data;
using Astra.Api.Security;
using Microsoft.Data.SqlClient;

namespace Astra.Api.Services;

public sealed class CoreDataService(Database database, EntraDirectory directory, IdentityOptions identity)
{
    private static readonly IReadOnlyDictionary<string, string> ReadQueries = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["roles"] = "SELECT code,name,description FROM dbo.roles ORDER BY code",
        ["permissions"] = "SELECT code,name,description FROM dbo.permissions ORDER BY name",
        ["ticket-sources"] = "SELECT code,name,active,sort_order FROM dbo.ticket_sources ORDER BY sort_order,name",
        ["categories"] = "SELECT id,parent_id,name,description,active,created_at,updated_at FROM dbo.categories ORDER BY parent_id,name",
        ["tags"] = "SELECT id,name,description,active,created_at,updated_at FROM dbo.tags ORDER BY name"
    };
    private static readonly IReadOnlyDictionary<string, string[]> ClientColumns = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["clients"] = ["id", "name", "ticket_prefix", "status", "primary_email", "primary_phone", "notes", "created_at", "updated_at"],
        ["contacts"] = ["id", "client_id", "site_id", "user_id", "name", "email", "phone", "active", "created_at", "updated_at"],
        ["sites"] = ["id", "client_id", "name", "address", "active", "created_at", "updated_at"],
        ["assets"] = ["id", "client_id", "site_id", "name", "asset_type", "serial_number", "external_reference", "active", "created_at", "updated_at"]
    };
    public static void RequireAdministrator(Actor actor) { if (!actor.Roles.Contains("ADMIN")) throw new ApiFault(403, "Administration access required."); }
    public async Task<IReadOnlyList<DirectoryUser>> ResolveEntraUserAsync(Actor actor, JsonObject input, CancellationToken cancellationToken)
    {
        RequireAdministrator(actor);
        if (input.Count != 1 || input["identifier"] is not JsonValue value || !value.TryGetValue<string>(out var identifier)
            || string.IsNullOrWhiteSpace(identifier) || identifier.Length > 200)
            throw new ApiFault(400, "Enter a name, work email or Entra UPN.");
        return await directory.SearchAsync(identifier, cancellationToken);
    }
    public async Task<object> ListAsync(Actor actor, string resource, Guid? requestedClient, CancellationToken cancellationToken)
    {
        if (resource is "roles" or "permissions" or "users" or "audit-logs") RequireAdministrator(actor);
        await using var connection = await database.OpenAsync(cancellationToken);
        if (ReadQueries.TryGetValue(resource, out var read)) return await Database.QueryAsync(connection, read, cancellationToken: cancellationToken);
        if (ClientColumns.TryGetValue(resource, out var columns))
        {
            var admin = actor.Roles.Contains("ADMIN");
            var ids = requestedClient.HasValue ? (admin || actor.ClientIds.Contains(requestedClient.Value) ? new[] { requestedClient.Value } : []) : actor.ClientIds.ToArray();
            var parameters = new Dictionary<string, object?> { ["all"] = admin && !requestedClient.HasValue, ["clients"] = JsonSerializer.Serialize(ids) };
            var key = resource == "clients" ? "id" : "client_id";
            return await Database.QueryAsync(connection, "SELECT " + string.Join(',', columns.Select(c => "[" + c + "]")) + " FROM dbo." + resource + " WHERE(@all=1 OR " + key + " IN(SELECT CONVERT(uniqueidentifier,[value]) FROM OPENJSON(@clients))) ORDER BY name", parameters, cancellationToken: cancellationToken);
        }
        if (resource == "audit-logs")
        {
            var rows = await Database.QueryAsync(connection, "SELECT TOP(200) id,actor_user_id,action,entity_type,entity_id,details,created_at FROM dbo.audit_logs ORDER BY id DESC", cancellationToken: cancellationToken);
            foreach (var row in rows) row["details"] = row["details"] is string value ? JsonNode.Parse(value) : null;
            return rows;
        }
        if (resource == "users")
        {
            var users = await Database.QueryAsync(connection, "SELECT id,email,display_name,active,last_login_at,work_scope,change_access,supervisor_user_id FROM dbo.users ORDER BY display_name", cancellationToken: cancellationToken);
            var roles = await Database.QueryAsync(connection, "SELECT user_id,role_code AS value FROM dbo.user_roles", cancellationToken: cancellationToken);
            var clients = await Database.QueryAsync(connection, "SELECT user_id,client_id AS value FROM dbo.client_users", cancellationToken: cancellationToken);
            var sites = await Database.QueryAsync(connection, "SELECT user_id,site_id AS value FROM dbo.user_sites", cancellationToken: cancellationToken);
            var permissions = await Database.QueryAsync(connection, "SELECT user_id,permission_code AS value FROM dbo.user_permissions", cancellationToken: cancellationToken);
            var departments = await Database.QueryAsync(connection, "SELECT user_id,department_id AS value,is_lead FROM dbo.department_users", cancellationToken: cancellationToken);
            foreach (var user in users)
            {
                var id = (Guid)user["id"]!;
                static object?[] Values(IEnumerable<Dictionary<string, object?>> rows, Guid id) => rows.Where(r => (Guid)r["user_id"]! == id).Select(r => r["value"]).Distinct().ToArray();
                user["roles"] = Values(roles, id); user["client_ids"] = Values(clients, id); user["site_ids"] = Values(sites, id); user["permissions"] = Values(permissions, id); user["department_ids"] = Values(departments, id); user["lead_department_ids"] = Values(departments.Where(r => (bool)r["is_lead"]!), id);
            }
            return users;
        }
        throw new ApiFault(404, "Not found.");
    }

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, (string Column, int Maximum, bool Required)>> InputFields = new Dictionary<string, IReadOnlyDictionary<string, (string, int, bool)>>(StringComparer.Ordinal)
    {
        ["clients"] = new Dictionary<string, (string, int, bool)> { { "name", ("name", 200, true) }, { "ticketPrefix", ("ticket_prefix", 8, false) }, { "primaryEmail", ("primary_email", 320, false) }, { "primaryPhone", ("primary_phone", 50, false) }, { "notes", ("notes", 4000, false) } },
        ["contacts"] = new Dictionary<string, (string, int, bool)> { { "clientId", ("client_id", 0, true) }, { "siteId", ("site_id", 0, false) }, { "name", ("name", 200, true) }, { "email", ("email", 320, false) }, { "phone", ("phone", 50, false) } },
        ["sites"] = new Dictionary<string, (string, int, bool)> { { "clientId", ("client_id", 0, true) }, { "name", ("name", 200, true) }, { "ticketPrefix", ("ticket_prefix", 16, false) }, { "address", ("address", 2000, false) } },
        ["assets"] = new Dictionary<string, (string, int, bool)> { { "clientId", ("client_id", 0, true) }, { "siteId", ("site_id", 0, false) }, { "name", ("name", 200, true) }, { "assetType", ("asset_type", 100, true) }, { "serialNumber", ("serial_number", 200, false) }, { "externalReference", ("external_reference", 500, false) } },
        ["categories"] = new Dictionary<string, (string, int, bool)> { { "parentId", ("parent_id", 0, false) }, { "name", ("name", 200, true) }, { "description", ("description", 2000, false) } },
        ["tags"] = new Dictionary<string, (string, int, bool)> { { "name", ("name", 100, true) }, { "description", ("description", 1000, false) } }
    };
    public static Dictionary<string, object?> Validate(string resource, JsonObject input)
    {
        if (!InputFields.TryGetValue(resource, out var fields)) throw new ApiFault(404, "Not found.");
        if (input.Any(p => !fields.ContainsKey(p.Key))) throw new ApiFault(400, "Unknown record fields.");
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (field, definition) in fields)
        {
            var node = input[field];
            if (node is null) { if (definition.Required) throw new ApiFault(400, "Missing field: " + field); values[definition.Column] = null; continue; }
            if (node is not JsonValue value || !value.TryGetValue<string>(out var text)) throw new ApiFault(400, "Invalid field: " + field);
            if (definition.Maximum == 0)
            { if (!Guid.TryParse(text, out var id)) throw new ApiFault(400, "Invalid field: " + field); values[definition.Column] = id; continue; }
            if (text.Length > definition.Maximum || (definition.Required && string.IsNullOrWhiteSpace(text))) throw new ApiFault(400, "Invalid field: " + field);
            if (field is "email" or "primaryEmail" && (!MailAddress.TryCreate(text, out var address) || address.Address != text || !address.Host.Contains('.', StringComparison.Ordinal))) throw new ApiFault(400, "Enter a valid email address.");
            if (field == "ticketPrefix" && (!Regex.IsMatch(text, "^[A-Z][A-Z0-9]{1," + (definition.Maximum - 1) + "}$", RegexOptions.CultureInvariant))) throw new ApiFault(400, "Invalid ticket prefix.");
            values[definition.Column] = field is "name" or "assetType" ? text.Trim() : text;
        }
        return values;
    }
    private static (string Code, string Name) ValidateTicketSource(JsonObject input)
    {
        if (input.Count != 2 || input["code"] is not JsonValue codeNode || !codeNode.TryGetValue<string>(out var code)
            || input["name"] is not JsonValue nameNode || !nameNode.TryGetValue<string>(out var name)
            || code.Length is < 1 or > 50 || !Regex.IsMatch(code, "^[A-Z0-9_]+$", RegexOptions.CultureInvariant)
            || name.Trim().Length is < 1 or > 100)
            throw new ApiFault(400, "Enter a valid source code and name.");
        return (code.Trim(), name.Trim());
    }
    public async Task<object> CreateTicketSourceAsync(Actor actor, JsonObject input, CancellationToken cancellationToken)
    {
        if (!actor.Permissions.Contains("*") && !actor.Permissions.Contains("SOURCES_MANAGE")) throw new ApiFault(403, "Permission required.");
        var (code, name) = ValidateTicketSource(input);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var values = new Dictionary<string, object?> { ["code"] = code, ["name"] = name, ["actor"] = actor.Id };
        await Database.ExecuteAsync(connection, "INSERT dbo.ticket_sources(code,name) VALUES(@code,@name)", values, transaction, cancellationToken);
        await Database.ExecuteAsync(connection, "INSERT dbo.audit_logs(actor_user_id,action,entity_type,entity_id,details) VALUES(@actor,'TICKET_SOURCE.CREATED','TICKET_SOURCE',@code,@details)", new Dictionary<string, object?> { ["actor"] = actor.Id, ["code"] = code, ["details"] = JsonSerializer.Serialize(new { name }, JsonSerializerOptions.Web) }, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new { code };
    }
    public async Task<object> UpdateTicketSourceAsync(Actor actor, string code, JsonObject input, CancellationToken cancellationToken)
    {
        if (!actor.Permissions.Contains("*") && !actor.Permissions.Contains("SOURCES_MANAGE")) throw new ApiFault(403, "Permission required.");
        if (string.IsNullOrWhiteSpace(code) || code.Length > 64 || input.Count != 2
            || input["name"] is not JsonValue nameNode || !nameNode.TryGetValue<string>(out var name) || name.Trim().Length is < 1 or > 100
            || input["active"] is not JsonValue activeNode || !activeNode.TryGetValue<bool>(out var active))
            throw new ApiFault(400, "Enter a valid source name and active state.");
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var values = new Dictionary<string, object?> { ["code"] = code, ["name"] = name.Trim(), ["active"] = active, ["actor"] = actor.Id };
        if (await Database.ExecuteAsync(connection, "UPDATE dbo.ticket_sources SET name=@name,active=@active,updated_at=SYSUTCDATETIME() WHERE code=@code", values, transaction, cancellationToken) == 0)
            throw new ApiFault(400, "Unknown ticket source.");
        await Database.ExecuteAsync(connection, "INSERT dbo.audit_logs(actor_user_id,action,entity_type,entity_id,details) VALUES(@actor,'TICKET_SOURCE.UPDATED','TICKET_SOURCE',@code,@details)", new Dictionary<string, object?> { ["actor"] = actor.Id, ["code"] = code, ["details"] = JsonSerializer.Serialize(new { name = name.Trim(), active }, JsonSerializerOptions.Web) }, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new { code };
    }
    public async Task<object> CreateUserAsync(Actor actor, ProvisionUserInput input, CancellationToken cancellationToken)
    {
        RequireAdministrator(actor);
        var role = input.Roles is { Length: 1 } ? input.Roles[0] : "";
        var validRoles = new[] { "USER", "INTERN", "TECHNICIAN", "TEAM_LEAD", "MANAGER", "ADMIN" };
        var validPermissions = new[] { "TICKETS_TEAM_VIEW", "TICKETS_ASSIGN", "TICKETS_APPROVE", "CATEGORY_MANAGE", "SLA_MANAGE", "REPORTS_VIEW", "USERS_MANAGE", "CLIENTS_MANAGE", "CONTACTS_MANAGE", "SITES_MANAGE", "TAGS_MANAGE", "AUDIT_VIEW" };
        var requestedPermissions = input.Permissions ?? [];
        var siteIds = input.SiteIds ?? [];
        var departmentIds = input.DepartmentIds ?? [];
        var leadDepartmentIds = input.LeadDepartmentIds ?? [];
        if (input.EntraObjectId == Guid.Empty || !validRoles.Contains(role) || input.ClientIds is null
            || requestedPermissions.Any(permission => !validPermissions.Contains(permission))
            || input.WorkScope is not (null or "INTERNAL" or "CLIENT" or "BOTH" or "ALL")
            || input.ChangeAccess is not (null or "NONE" or "CONTRIBUTOR" or "COORDINATOR" or "APPROVER" or "CHANGE_MANAGER")
            || input.Expertise?.Length > 1000)
            throw new ApiFault(400, "Invalid user provisioning details.");

        var internalClient = Guid.Parse("00000000-0000-4000-8000-000000000132");
        var externalClients = input.ClientIds.Where(id => id != internalClient).Distinct().ToArray();
        if (role == "USER" && externalClients.Length == 0) throw new ApiFault(400, "Client users require client membership.");
        var scope = role == "ADMIN" ? "ALL" : input.WorkScope ?? "CLIENT";
        var changeAccess = role == "ADMIN" ? "CHANGE_MANAGER" : input.ChangeAccess ?? "NONE";
        if (role == "INTERN" && scope is not ("INTERNAL" or "BOTH")) throw new ApiFault(400, "Interns require Internal or Both work scope.");
        if (role != "ADMIN" && input.WorkScope is not null && (scope is "CLIENT" or "BOTH") && externalClients.Length == 0) throw new ApiFault(400, "Client or Both work scope requires at least one client.");
        if (role == "INTERN" && input.SupervisorUserId is null) throw new ApiFault(400, "Interns require a supervisor.");
        if (leadDepartmentIds.Except(departmentIds).Any()) throw new ApiFault(400, "Lead departments must also be assigned departments.");
        var effectiveClients = externalClients.Concat(scope is "INTERNAL" or "BOTH" or "ALL" ? [internalClient] : []).Distinct().ToArray();

        var directoryUser = await directory.FindByIdAsync(input.EntraObjectId, cancellationToken);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var values = new Dictionary<string, object?> { ["entra"] = input.EntraObjectId, ["tenant"] = Guid.Parse(identity.TenantId!), ["email"] = directoryUser.Email, ["name"] = directoryUser.DisplayName, ["scope"] = scope, ["change"] = changeAccess, ["supervisor"] = input.SupervisorUserId, ["actor"] = actor.Id };
        if (input.SupervisorUserId.HasValue)
        {
            var supervisor = await Database.QueryAsync(connection, "SELECT id FROM dbo.users WHERE id=@supervisor AND active=1", values, transaction, cancellationToken);
            if (supervisor.Count == 0) throw new ApiFault(400, "Choose an active supervisor.");
        }
        foreach (var clientId in effectiveClients)
        {
            var client = await Database.QueryAsync(connection, "SELECT id FROM dbo.clients WHERE id=@client AND status='ACTIVE'", new Dictionary<string, object?> { ["client"] = clientId }, transaction, cancellationToken);
            if (client.Count == 0) throw new ApiFault(400, "Choose an active client organization.");
        }
        var id = Guid.NewGuid(); values["id"] = id;
        await Database.ExecuteAsync(connection, "INSERT dbo.users(id,entra_object_id,entra_tenant_id,email,display_name,work_scope,change_access,supervisor_user_id) VALUES(@id,@entra,@tenant,@email,@name,@scope,@change,@supervisor)", values, transaction, cancellationToken);
        await Database.ExecuteAsync(connection, "INSERT dbo.user_roles(user_id,role_code,granted_by) VALUES(@id,@role,@actor)", new Dictionary<string, object?> { ["id"] = id, ["role"] = role, ["actor"] = actor.Id }, transaction, cancellationToken);
        foreach (var clientId in effectiveClients)
            await Database.ExecuteAsync(connection, "INSERT dbo.client_users(client_id,user_id) VALUES(@client,@id)", new Dictionary<string, object?> { ["client"] = clientId, ["id"] = id }, transaction, cancellationToken);
        foreach (var siteId in siteIds.Distinct())
            if (await Database.ExecuteAsync(connection, "INSERT dbo.user_sites(user_id,site_id) SELECT @id,id FROM dbo.sites WHERE id=@site AND client_id IN(SELECT CONVERT(uniqueidentifier,[value]) FROM OPENJSON(@clients))", new Dictionary<string, object?> { ["id"] = id, ["site"] = siteId, ["clients"] = JsonSerializer.Serialize(effectiveClients) }, transaction, cancellationToken) == 0)
                throw new ApiFault(400, "Site access must belong to an assigned client.");

        var granted = role switch
        {
            "TECHNICIAN" => new[] { "TICKETS_TEAM_VIEW", "TICKETS_ASSIGN" },
            "TEAM_LEAD" => new[] { "TICKETS_TEAM_VIEW", "TICKETS_ASSIGN", "CATEGORY_MANAGE", "TAGS_MANAGE", "REPORTS_VIEW" },
            "MANAGER" => new[] { "TICKETS_TEAM_VIEW", "TICKETS_ASSIGN", "TICKETS_APPROVE", "CATEGORY_MANAGE", "TAGS_MANAGE", "SLA_MANAGE", "REPORTS_VIEW", "AUDIT_VIEW" },
            _ => []
        };
        foreach (var permission in granted)
            await Database.ExecuteAsync(connection, "INSERT dbo.user_permissions(user_id,permission_code,granted_by) VALUES(@id,@permission,@actor)", new Dictionary<string, object?> { ["id"] = id, ["permission"] = permission, ["actor"] = actor.Id }, transaction, cancellationToken);
        var leadDepartments = leadDepartmentIds.ToHashSet();
        foreach (var departmentId in departmentIds.Distinct())
            if (await Database.ExecuteAsync(connection, "INSERT dbo.department_users(department_id,user_id,expertise,is_lead) SELECT id,@user,@expertise,@lead FROM dbo.departments WHERE id=@department AND active=1", new Dictionary<string, object?> { ["user"] = id, ["expertise"] = string.IsNullOrWhiteSpace(input.Expertise) ? null : input.Expertise.Trim(), ["lead"] = leadDepartments.Contains(departmentId), ["department"] = departmentId }, transaction, cancellationToken) == 0)
                throw new ApiFault(400, "Choose active departments.");
        var auditDetails = JsonSerializer.Serialize(new { roles = new[] { role }, clientIds = effectiveClients, workScope = scope, changeAccess, siteIds, permissions = granted, departmentIds, leadDepartmentIds }, JsonSerializerOptions.Web);
        await Database.ExecuteAsync(connection, "INSERT dbo.audit_logs(actor_user_id,action,entity_type,entity_id,details) VALUES(@actor,'USER.CREATED','USER',CONVERT(nvarchar(36),@id),@details)", new Dictionary<string, object?> { ["actor"] = actor.Id, ["id"] = id, ["details"] = auditDetails }, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new { id };
    }
    public async Task<object> CreateAsync(Actor actor, string resource, JsonObject input, CancellationToken cancellationToken)
    {
        if (resource == "categories")
        { if (!actor.Permissions.Contains("*") && !actor.Permissions.Contains("CATEGORY_MANAGE")) throw new ApiFault(403, "Permission required."); }
        else RequireAdministrator(actor);
        var values = Validate(resource, input); var id = Guid.NewGuid(); values["id"] = id;
        await using var connection = await database.OpenAsync(cancellationToken); await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        if (resource == "clients")
        {
            var requested = values["ticket_prefix"] as string;
            if (requested is null)
            {
                var letters = Regex.Replace((string)values["name"]!, "[^a-z0-9]", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                var basis = letters.Length > 0 ? letters[..Math.Min(3, letters.Length)].ToUpperInvariant() : "CLT"; var prefix = basis.Length >= 2 ? basis : basis + "X";
                await Database.ExecuteAsync(connection, "DECLARE @result int; EXEC @result=sys.sp_getapplock @Resource=N'ASTRA customer prefix',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000; IF @result<0 THROW 51000,'Prefix unavailable',1;", transaction: transaction, cancellationToken: cancellationToken);
                for (long suffix = 2; ; suffix++)
                {
                    var existing = await Database.QueryAsync(connection, "SELECT prefix FROM dbo.ticket_prefix_registry WITH(UPDLOCK,HOLDLOCK) WHERE prefix=@prefix", new Dictionary<string, object?> { ["prefix"] = prefix }, transaction, cancellationToken);
                    if (existing.Count == 0) break; prefix = basis[..Math.Min(6, basis.Length)] + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (prefix.Length > 8) throw new ApiFault(409, "Choose a customer ticket prefix.");
                }
                requested = prefix;
            }
            values["ticket_prefix"] = await TicketNumbering.ReserveAsync(connection, transaction, "CLIENT", id, requested, cancellationToken: cancellationToken);
        }
        if (resource == "sites") values["ticket_prefix"] = await TicketNumbering.ReserveAsync(connection, transaction, "SITE", id, values["ticket_prefix"] as string, (Guid)values["client_id"]!, cancellationToken);
        if (resource == "contacts" && values["site_id"] is Guid site)
        {
            var valid = await Database.QueryAsync(connection, "SELECT id FROM dbo.sites WITH(HOLDLOCK) WHERE id=@site_id AND client_id=@client_id AND active=1", values, transaction, cancellationToken);
            if (valid.Count == 0) throw new ApiFault(400, "Site does not belong to the selected organization.");
        }
        var columns = values.Keys.ToArray();
        await Database.ExecuteAsync(connection, "INSERT dbo." + resource + "(" + string.Join(',', columns.Select(c => "[" + c + "]")) + ") VALUES(" + string.Join(',', columns.Select(c => "@" + c)) + ")", values, transaction, cancellationToken);
        if (resource == "clients") await Database.ExecuteAsync(connection, "INSERT dbo.client_ticket_sequences(client_id,next_number) VALUES(@id,1)", values, transaction, cancellationToken);
        var type = resource switch { "clients" => "CLIENT", "contacts" => "CONTACT", "sites" => "SITE", "assets" => "ASSET", "categories" => "CATEGORY", "tags" => "TAG", _ => throw new ApiFault(404, "Not found.") };
        object details = resource == "clients" ? new { name = values["name"], ticketPrefix = values["ticket_prefix"] } : resource == "contacts" ? new { clientId = values["client_id"], siteId = values["site_id"] } : resource is "sites" or "assets" ? new { clientId = values["client_id"] } : new { };
        await Audit.WriteAsync(connection, transaction, actor.Id, type + ".CREATED", type, id, details, cancellationToken);
        await transaction.CommitAsync(cancellationToken); return new { id };
    }

}
