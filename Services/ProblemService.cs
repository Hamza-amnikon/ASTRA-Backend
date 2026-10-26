using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Api.Data;
using Astra.Api.Security;
using Microsoft.Data.SqlClient;

namespace Astra.Api.Services;

public sealed class ProblemService(Database database)
{
    private static readonly string[] Roles = ["TECHNICIAN", "TEAM_LEAD", "MANAGER", "ADMIN"];
    private static readonly string[] Statuses = ["OPEN", "INVESTIGATING", "KNOWN_ERROR", "RESOLVED", "CLOSED"];
    private static readonly string[] Priorities = TicketReadService.Priorities;

    private static bool Manager(Actor actor) => actor.Roles.Overlaps(["ADMIN", "MANAGER", "TEAM_LEAD"]);
    private static void RequireStaff(Actor actor)
    {
        if (!actor.Roles.Overlaps(Roles)) throw new ApiFault(403, "Problem Management is available to support staff.");
    }

    private static Dictionary<string, object?> Parameters(Actor actor)
    {
        var values = ScopeSql.Parameters(actor);
        values["actor"] = actor.Id;
        values["wide"] = Manager(actor);
        values["team"] = TicketReadService.Permission(actor, "TICKETS_TEAM_VIEW");
        values["departments"] = JsonSerializer.Serialize(actor.DepartmentIds ?? []);
        return values;
    }

    private static string Visible(string alias = "p") => ScopeSql.Records(alias);
    private static async Task<Dictionary<string, object?>> VisibleProblemAsync(SqlConnection connection, Actor actor, Guid id, SqlTransaction? transaction, CancellationToken cancellationToken, bool lockRow = false)
    {
        var values = Parameters(actor); values["id"] = id;
        var rows = await Database.QueryAsync(connection,
            $"SELECT p.*,c.name AS client_name,u.display_name AS owner_name FROM dbo.problems p{(lockRow ? " WITH(UPDLOCK,HOLDLOCK)" : "")} JOIN dbo.clients c ON c.id=p.client_id JOIN dbo.users u ON u.id=p.owner_user_id WHERE {Visible()} AND p.id=@id",
            values, transaction, cancellationToken);
        if (rows.Count == 0) throw new ApiFault(404, "Problem not found.");
        return rows[0];
    }

    private static Task HistoryAsync(SqlConnection connection, SqlTransaction transaction, Guid problemId, Guid actor, string action, object details, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(details, JsonSerializerOptions.Web);
        return Database.ExecuteAsync(connection,
            "INSERT dbo.problem_history(problem_id,actor_user_id,action,details) VALUES(@id,@actor,@action,@details)",
            new Dictionary<string, object?> { ["id"] = problemId, ["actor"] = actor, ["action"] = action, ["details"] = json }, transaction, cancellationToken);
    }

    public async Task<object> OptionsAsync(Actor actor, CancellationToken cancellationToken)
    {
        RequireStaff(actor);
        var values = ScopeSql.Parameters(actor);
        await using var connection = await database.OpenAsync(cancellationToken);
        var clients = await Database.QueryAsync(connection,
            "SELECT id,name FROM dbo.clients WHERE status='ACTIVE' AND organization_type='CLIENT' AND (@scopeAdmin=1 OR id IN(SELECT CONVERT(uniqueidentifier,[value]) FROM OPENJSON(@scopeClients))) ORDER BY name", values, cancellationToken: cancellationToken);
        var sites = await Database.QueryAsync(connection,
            "SELECT id,client_id,name FROM dbo.sites WHERE active=1 AND (@scopeAdmin=1 OR (client_id IN(SELECT CONVERT(uniqueidentifier,[value]) FROM OPENJSON(@scopeClients)) AND (@scopeSiteCount=0 OR id IN(SELECT CONVERT(uniqueidentifier,[value]) FROM OPENJSON(@scopeSites))))) ORDER BY name", values, cancellationToken: cancellationToken);
        return new { clients, sites };
    }

    public async Task<object> ListAsync(Actor actor, string? query, string? status, Guid? clientId, int page, int pageSize, CancellationToken cancellationToken)
    {
        RequireStaff(actor);
        query = (query ?? "").Trim();
        if (query.Length > 200 || page is < 1 or > 100000 || pageSize is < 1 or > 100 || (status is not null && !Statuses.Contains(status)))
            throw new ApiFault(400, "Invalid problem list filters.");
        var values = Parameters(actor);
        values["client"] = clientId; values["status"] = status;
        values["pattern"] = "%" + query.Replace("~", "~~").Replace("%", "~%").Replace("_", "~_") + "%";
        values["offset"] = (page - 1) * pageSize; values["take"] = pageSize;
        var scope = Visible() + " AND (@client IS NULL OR p.client_id=@client) AND (@status IS NULL OR p.status=@status) AND (@q=N'' OR p.problem_number LIKE @pattern ESCAPE N'~' OR p.title LIKE @pattern ESCAPE N'~')";
        await using var connection = await database.OpenAsync(cancellationToken);
        var count = await Database.QueryAsync(connection,
            "SELECT COUNT_BIG(*) AS total FROM dbo.problems p WHERE " + scope,
            new Dictionary<string, object?>(values) { ["q"] = query }, cancellationToken: cancellationToken);
        var items = await Database.QueryAsync(connection,
            "SELECT p.*,c.name AS client_name,u.display_name AS owner_name FROM dbo.problems p JOIN dbo.clients c ON c.id=p.client_id JOIN dbo.users u ON u.id=p.owner_user_id WHERE " + scope + " ORDER BY p.updated_at DESC,p.id OFFSET @offset ROWS FETCH NEXT @take ROWS ONLY",
            new Dictionary<string, object?>(values) { ["q"] = query }, cancellationToken: cancellationToken);
        return new { total = Convert.ToInt64(count[0]["total"]), items };
    }

    public async Task<object> CreateAsync(Actor actor, CreateProblemInput input, CancellationToken cancellationToken)
    {
        RequireStaff(actor);
        if (input.Title is null || input.Title.Trim().Length is < 3 or > 200 || input.Description is null || input.Description.Trim().Length is < 3 or > 10000 || !Priorities.Contains(input.Priority))
            throw new ApiFault(400, "Enter a title and description and choose a valid priority.");
        if (!actor.Roles.Contains("ADMIN") && !actor.ClientIds.Contains(input.ClientId)) throw new ApiFault(404, "Client not found.");
        if ((actor.SiteIds.Count > 0) && input.SiteId is null && !actor.Roles.Contains("ADMIN")) throw new ApiFault(400, "Select a site within your access scope.");

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var values = new Dictionary<string, object?> { ["client"] = input.ClientId, ["site"] = input.SiteId, ["title"] = input.Title.Trim(), ["description"] = input.Description.Trim(), ["priority"] = input.Priority, ["actor"] = actor.Id };
        var client = await Database.QueryAsync(connection, "SELECT id FROM dbo.clients WITH(HOLDLOCK) WHERE id=@client AND status='ACTIVE' AND organization_type='CLIENT'", values, transaction, cancellationToken);
        if (client.Count == 0) throw new ApiFault(404, "Client not found.");
        if (input.SiteId.HasValue)
        {
            var site = await Database.QueryAsync(connection, "SELECT id FROM dbo.sites WHERE id=@site AND client_id=@client AND active=1", values, transaction, cancellationToken);
            if (site.Count == 0 || (!actor.Roles.Contains("ADMIN") && actor.SiteIds.Count > 0 && !actor.SiteIds.Contains(input.SiteId.Value))) throw new ApiFault(404, "Site not found.");
        }
        var id = Guid.NewGuid(); values["id"] = id;
        await Database.ExecuteAsync(connection,
            "INSERT dbo.problems(id,client_id,site_id,title,description,priority,owner_user_id,created_by_user_id) VALUES(@id,@client,@site,@title,@description,@priority,@actor,@actor)", values, transaction, cancellationToken);
        await HistoryAsync(connection, transaction, id, actor.Id, "PROBLEM.CREATED", new { }, cancellationToken);
        await Audit.WriteAsync(connection, transaction, actor.Id, "PROBLEM.CREATED", "PROBLEM", id, new { clientId = input.ClientId, siteId = input.SiteId }, cancellationToken);
        var created = await VisibleProblemAsync(connection, actor, id, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return created;
    }

    public async Task<object> DetailAsync(Actor actor, Guid id, CancellationToken cancellationToken)
    {
        RequireStaff(actor);
        await using var connection = await database.OpenAsync(cancellationToken);
        var values = Parameters(actor); values["id"] = id;
        var problem = await VisibleProblemAsync(connection, actor, id, null, cancellationToken);
        var linkedTickets = await Database.QueryAsync(connection,
            "SELECT t.id,t.ticket_number,t.subject,t.status,t.priority FROM dbo.problem_tickets pt JOIN dbo.tickets t ON t.id=pt.ticket_id WHERE pt.problem_id=@id AND " + TicketReadService.Access(), values, cancellationToken: cancellationToken);
        var history = await Database.QueryAsync(connection,
            "SELECT h.id,h.action,h.details,h.created_at,u.display_name AS actor_name FROM dbo.problem_history h JOIN dbo.users u ON u.id=h.actor_user_id WHERE h.problem_id=@id ORDER BY h.created_at DESC,h.id DESC OFFSET 0 ROWS FETCH NEXT 200 ROWS ONLY", values, cancellationToken: cancellationToken);
        foreach (var row in history) row["details"] = row["details"] is string json ? JsonNode.Parse(json) : null;
        var owners = Manager(actor) ? await Database.QueryAsync(connection, """
            SELECT DISTINCT u.id,u.display_name FROM dbo.users u JOIN dbo.user_roles ur ON ur.user_id=u.id
            WHERE u.active=1 AND ur.role_code IN('TECHNICIAN','TEAM_LEAD','MANAGER','ADMIN')
             AND (EXISTS(SELECT 1 FROM dbo.user_roles ar WHERE ar.user_id=u.id AND ar.role_code='ADMIN') OR EXISTS(SELECT 1 FROM dbo.client_users uc WHERE uc.user_id=u.id AND uc.client_id=@client))
             AND (NOT EXISTS(SELECT 1 FROM dbo.user_sites us WHERE us.user_id=u.id) OR EXISTS(SELECT 1 FROM dbo.user_sites us WHERE us.user_id=u.id AND us.site_id=@site))
            ORDER BY u.display_name
            """, new Dictionary<string, object?> { ["client"] = problem["client_id"], ["site"] = problem["site_id"] }, cancellationToken: cancellationToken) : [];
        var canEdit = actor.Workspace != "admin" && (Manager(actor) || (Guid)problem["owner_user_id"]! == actor.Id);
        return new { problem, tickets = linkedTickets, history, owners, canEdit, canManage = actor.Workspace != "admin" && Manager(actor) };
    }

    public static void ValidateUpdate(UpdateProblemInput input, Dictionary<string, object?> current, Actor actor)
    {
        RequireStaff(actor);
        if (input.Revision != Convert.ToInt32(current["revision"])) throw new ApiFault(409, "This problem changed. Reload before saving.");
        var isManager = Manager(actor);
        if (!isManager && (Guid)current["owner_user_id"]! != actor.Id) throw new ApiFault(403, "Only the owner or a manager can edit this investigation.");
        if (!isManager && input.OwnerUserId != (Guid)current["owner_user_id"]!) throw new ApiFault(403, "Reassignment requires the Management workspace.");
        if (!isManager && (input.Status == "CLOSED" || (string)current["status"]! == "CLOSED")) throw new ApiFault(403, "Closing or reopening requires the Management workspace.");
        var transitions = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["OPEN"] = ["OPEN", "INVESTIGATING", "KNOWN_ERROR", "RESOLVED"],
            ["INVESTIGATING"] = ["INVESTIGATING", "OPEN", "KNOWN_ERROR", "RESOLVED"],
            ["KNOWN_ERROR"] = ["KNOWN_ERROR", "INVESTIGATING", "RESOLVED"],
            ["RESOLVED"] = ["RESOLVED", "INVESTIGATING", "CLOSED"],
            ["CLOSED"] = ["CLOSED", "INVESTIGATING"]
        };
        if (!Statuses.Contains(input.Status) || !transitions[(string)current["status"]!].Contains(input.Status)) throw new ApiFault(400, "Invalid problem status transition.");
        if (input.Title is null || input.Title.Trim().Length is < 3 or > 200 || input.Description is null || input.Description.Trim().Length is < 3 or > 10000 || !Priorities.Contains(input.Priority) || input.RootCause is null || input.Workaround is null || input.Resolution is null || input.RootCause.Length > 10000 || input.Workaround.Length > 10000 || input.Resolution.Length > 10000) throw new ApiFault(400, "Invalid problem details.");
        if (new[] { "KNOWN_ERROR", "RESOLVED", "CLOSED" }.Contains(input.Status) && input.RootCause.Trim().Length < 3) throw new ApiFault(400, "Record the root cause first.");
        if (input.Status == "KNOWN_ERROR" && input.Workaround.Trim().Length < 3) throw new ApiFault(400, "Record a usable workaround before marking a known error.");
        if (new[] { "RESOLVED", "CLOSED" }.Contains(input.Status) && input.Resolution.Trim().Length < 3) throw new ApiFault(400, "Record the resolution first.");
    }

    public async Task<object> UpdateAsync(Actor actor, Guid id, UpdateProblemInput input, CancellationToken cancellationToken)
    {
        RequireStaff(actor);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var current = await VisibleProblemAsync(connection, actor, id, transaction, cancellationToken, lockRow: true);
        ValidateUpdate(input, current, actor);
        var values = new Dictionary<string, object?> { ["id"] = id, ["title"] = input.Title.Trim(), ["description"] = input.Description.Trim(), ["priority"] = input.Priority, ["status"] = input.Status, ["cause"] = input.RootCause.Trim(), ["workaround"] = input.Workaround.Trim(), ["resolution"] = input.Resolution.Trim(), ["owner"] = input.OwnerUserId, ["client"] = current["client_id"], ["site"] = current["site_id"] };
        var owner = await Database.QueryAsync(connection, """
            SELECT u.id FROM dbo.users u WHERE u.id=@owner AND u.active=1
              AND EXISTS(SELECT 1 FROM dbo.user_roles ur WHERE ur.user_id=u.id AND ur.role_code IN('TECHNICIAN','TEAM_LEAD','MANAGER','ADMIN'))
              AND (EXISTS(SELECT 1 FROM dbo.user_roles ar WHERE ar.user_id=u.id AND ar.role_code='ADMIN') OR EXISTS(SELECT 1 FROM dbo.client_users uc WHERE uc.user_id=u.id AND uc.client_id=@client))
              AND (NOT EXISTS(SELECT 1 FROM dbo.user_sites us WHERE us.user_id=u.id) OR EXISTS(SELECT 1 FROM dbo.user_sites us WHERE us.user_id=u.id AND us.site_id=@site))
            """, values, transaction, cancellationToken);
        if (owner.Count == 0 && input.OwnerUserId != (Guid)current["owner_user_id"]!) throw new ApiFault(400, "Choose an active support owner with access to this client.");
        await Database.ExecuteAsync(connection, """
            UPDATE dbo.problems SET title=@title,description=@description,priority=@priority,status=@status,root_cause=@cause,workaround=@workaround,resolution=@resolution,owner_user_id=@owner,revision=revision+1,updated_at=SYSUTCDATETIME()
            WHERE id=@id AND revision=@revision
            """, new Dictionary<string, object?>(values) { ["revision"] = input.Revision }, transaction, cancellationToken);
        await HistoryAsync(connection, transaction, id, actor.Id, "PROBLEM.UPDATED", new { previousStatus = current["status"], status = input.Status, ownerUserId = input.OwnerUserId, revision = input.Revision + 1 }, cancellationToken);
        await Audit.WriteAsync(connection, transaction, actor.Id, "PROBLEM.UPDATED", "PROBLEM", id, new { previousStatus = current["status"], status = input.Status, ownerUserId = input.OwnerUserId }, cancellationToken);
        var result = await VisibleProblemAsync(connection, actor, id, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<object> LinkAsync(Actor actor, Guid id, Guid ticketId, bool remove, CancellationToken cancellationToken)
    {
        RequireStaff(actor);
        var ticketValues = TicketReadService.Parameters(actor); ticketValues["id"] = ticketId;
        await using var connection = await database.OpenAsync(cancellationToken);
        var ticket = await Database.QueryAsync(connection, "SELECT t.id,t.client_id FROM dbo.tickets t WHERE t.id=@id AND " + TicketReadService.Access(), ticketValues, cancellationToken: cancellationToken);
        if (ticket.Count == 0) throw new ApiFault(404, "Ticket not found.");
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var current = await VisibleProblemAsync(connection, actor, id, transaction, cancellationToken, lockRow: true);
        if (!Manager(actor) && (Guid)current["owner_user_id"]! != actor.Id) throw new ApiFault(403, "Only the problem owner or a manager can change linked tickets.");
        if ((string)current["status"]! == "CLOSED") throw new ApiFault(400, "Reopen the problem before changing links.");
        if ((Guid)ticket[0]["client_id"]! != (Guid)current["client_id"]!) throw new ApiFault(400, "The ticket must belong to the same client.");
        if (!remove)
        {
            var count = await Database.QueryAsync(connection, "SELECT COUNT(*) AS total FROM dbo.problem_tickets WHERE problem_id=@id", new Dictionary<string, object?> { ["id"] = id }, transaction, cancellationToken);
            if (Convert.ToInt32(count[0]["total"]) >= 100) throw new ApiFault(400, "A problem can link up to 100 tickets.");
            var inserted = await Database.ExecuteAsync(connection, "IF NOT EXISTS(SELECT 1 FROM dbo.problem_tickets WITH(UPDLOCK,HOLDLOCK) WHERE problem_id=@id AND ticket_id=@ticket) INSERT dbo.problem_tickets(problem_id,ticket_id,client_id,linked_by_user_id) VALUES(@id,@ticket,@client,@actor)", new Dictionary<string, object?> { ["id"] = id, ["ticket"] = ticketId, ["client"] = current["client_id"], ["actor"] = actor.Id }, transaction, cancellationToken);
            if (inserted > 0) await HistoryAsync(connection, transaction, id, actor.Id, "PROBLEM.TICKET_LINKED", new { ticketId }, cancellationToken);
        }
        else
        {
            var deleted = await Database.ExecuteAsync(connection, "DELETE dbo.problem_tickets WHERE problem_id=@id AND ticket_id=@ticket", new Dictionary<string, object?> { ["id"] = id, ["ticket"] = ticketId }, transaction, cancellationToken);
            if (deleted > 0) await HistoryAsync(connection, transaction, id, actor.Id, "PROBLEM.TICKET_UNLINKED", new { ticketId }, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new { ok = true };
    }

    public async Task<object> NoteAsync(Actor actor, Guid id, string body, CancellationToken cancellationToken)
    {
        RequireStaff(actor);
        if (body is null || body.Trim().Length is < 2 or > 5000) throw new ApiFault(400, "Enter an investigation note.");
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var current = await VisibleProblemAsync(connection, actor, id, transaction, cancellationToken, lockRow: true);
        if ((string)current["status"]! == "CLOSED") throw new ApiFault(400, "Reopen the problem before adding notes.");
        await HistoryAsync(connection, transaction, id, actor.Id, "PROBLEM.NOTE", new { body = body.Trim() }, cancellationToken);
        await Audit.WriteAsync(connection, transaction, actor.Id, "PROBLEM.NOTE", "PROBLEM", id, new { }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new { ok = true };
    }


}
