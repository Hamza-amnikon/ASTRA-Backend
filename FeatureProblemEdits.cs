using System.Text.Json;

public static partial class FeatureApis
{
    private static void MapProblemEdits(RouteGroupBuilder api)
    {
        api.MapPatch("/problems/{id:guid}", async (string id, JsonElement b, HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.EditProblem(Actor(c), id, b, false, t)));
        api.MapPost("/problems/{id:guid}/notes", async (string id, JsonElement b, HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.EditProblem(Actor(c), id, b, true, t)));
    }
}
public sealed partial class MySqlStore
{
    public async Task<object> EditProblem(string actor, string id, JsonElement body, bool note, CancellationToken ct)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        var (admin, staff, manager) = await GetProblemRoles(connection, actor, ct);
        if (!staff)
            throw new UnauthorizedAccessException();
        await using var tx = await connection.BeginTransactionAsync(ct);
        var row = (await FRows(connection, tx, $"SELECT p.* FROM problems p WHERE p.id=@id AND {FScope("p")} FOR UPDATE", ct, ("id", id), ("actor", actor), ("admin", admin))).FirstOrDefault() ?? throw new KeyNotFoundException();
        if (note)
        {
            if ((string)row["status"]! == "CLOSED")
                throw new ArgumentException("Reopen the problem before adding notes.");
            var text = FText(JString(body, "body"), "Investigation note", 2, 5000);
            await FExecute(connection, tx, "INSERT INTO problem_history(problem_id,actor_user_id,action,details) VALUES(@id,@actor,'PROBLEM.NOTE',JSON_OBJECT('body',@body))", ct, ("id", id), ("actor", actor), ("body", text));
        }
        else
        {
            if (!manager && !Equals(row["owner_user_id"], actor))
                throw new UnauthorizedAccessException();
            if (JInt(body, "revision") != FInt(row["revision"]))
                throw new InvalidOperationException("This problem changed. Reload before saving.");
            var title = FText(JString(body, "title"), "Title", 3, 200);
            var description = FText(JString(body, "description"), "Description", 3, 10000);
            var priority = JChoice(body, "priority", "P1_CRITICAL", "P2_HIGH", "P3_MEDIUM", "P4_LOW");
            var status = JChoice(body, "status", "OPEN", "INVESTIGATING", "KNOWN_ERROR", "RESOLVED", "CLOSED");
            var owner = FGuid(JString(body, "ownerUserId"), "owner");
            var root = FText(JString(body, "rootCause"), "Root cause", 0, 10000);
            var workaround = FText(JString(body, "workaround"), "Workaround", 0, 10000);
            var resolution = FText(JString(body, "resolution"), "Resolution", 0, 10000);
            var old = (string)row["status"]!;
            if (!manager && (owner != (string)row["owner_user_id"]! || status == "CLOSED" || old == "CLOSED"))
                throw new UnauthorizedAccessException();
            string[] allowed = old switch
            {
                "OPEN" => ["OPEN", "INVESTIGATING", "KNOWN_ERROR", "RESOLVED"],
                "INVESTIGATING" => ["INVESTIGATING", "OPEN", "KNOWN_ERROR", "RESOLVED"],
                "KNOWN_ERROR" => ["KNOWN_ERROR", "INVESTIGATING", "RESOLVED"],
                "RESOLVED" => ["RESOLVED", "INVESTIGATING", "CLOSED"],
                "CLOSED" => ["CLOSED", "INVESTIGATING"],
                _ => []
            };
            if (!allowed.Contains(status))
                throw new ArgumentException("Invalid problem status transition.");
            if (status is "KNOWN_ERROR" or "RESOLVED" or "CLOSED" && root.Length < 3)
                throw new ArgumentException("Record the root cause first.");
            if (status == "KNOWN_ERROR" && workaround.Length < 3)
                throw new ArgumentException("Record a usable workaround first.");
            if (status is "RESOLVED" or "CLOSED" && resolution.Length < 3)
                throw new ArgumentException("Record the resolution first.");
            if (!Equals(row["owner_user_id"], owner) && (await FRows(connection, tx, "SELECT u.id FROM users u WHERE u.id=@owner AND u.active=1 AND EXISTS(SELECT 1 FROM user_roles r WHERE r.user_id=u.id AND r.role_code IN ('ADMIN','MANAGER','TEAM_LEAD','TECHNICIAN')) AND (EXISTS(SELECT 1 FROM user_roles r WHERE r.user_id=u.id AND r.role_code='ADMIN') OR EXISTS(SELECT 1 FROM client_users cu WHERE cu.user_id=u.id AND cu.client_id=@client)) AND (NOT EXISTS(SELECT 1 FROM user_sites us WHERE us.user_id=u.id) OR EXISTS(SELECT 1 FROM user_sites us WHERE us.user_id=u.id AND us.site_id=@site))", ct, ("owner", owner), ("client", row["client_id"]), ("site", row["site_id"]))).Count == 0)
                throw new ArgumentException("Choose an active support owner with access to this client and site.");
            await FExecute(connection, tx, "UPDATE problems SET title=@title,description=@description,priority=@priority,status=@status,root_cause=@root,workaround=@workaround,resolution=@resolution,owner_user_id=@owner,revision=revision+1,updated_at=UTC_TIMESTAMP(6) WHERE id=@id", ct, ("id", id), ("title", title), ("description", description), ("priority", priority), ("status", status), ("root", root), ("workaround", workaround), ("resolution", resolution), ("owner", owner));
            await FExecute(connection, tx, "INSERT INTO problem_history(problem_id,actor_user_id,action,details) VALUES(@id,@actor,'PROBLEM.UPDATED',JSON_OBJECT('previousStatus',@old,'status',@status,'ownerUserId',@owner))", ct, ("id", id), ("actor", actor), ("old", old), ("status", status), ("owner", owner));
        }
        await AddAudit(connection, tx, actor, note ? "PROBLEM.NOTE" : "PROBLEM.UPDATED", "PROBLEM", id, new
        {
        }, ct);
        await tx.CommitAsync(ct);
        return note ? new
        {
            ok = true
        } : (object)((await FQuery("SELECT * FROM problems WHERE id=@id", ct, ("id", id)))[0]);
    }
}
