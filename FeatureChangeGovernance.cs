using System.Text.Json;

public static partial class FeatureApis
{
    private static void MapChangeGovernance(RouteGroupBuilder api)
    {
        api.MapGet("/change-governance", async (HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.ChangeConfiguration(Actor(c), t)));
        foreach (var kind in new[] { "groups", "templates", "blackouts" })
        {
            var resource = kind;
            api.MapPost("/change-governance/" + resource, async (JsonElement body, HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.SaveChangeConfiguration(Actor(c), resource, null, body, t)));
            api.MapPatch("/change-governance/" + resource + "/{id:guid}", async (string id, JsonElement body, HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.SaveChangeConfiguration(Actor(c), resource, id, body, t)));
        }
        api.MapPatch("/change-governance/policy", async (JsonElement body, HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.SaveChangeConfiguration(Actor(c), "policy", null, body, t)));
        api.MapGet("/change-governance/{id:guid}", async (string id, HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.ChangeGovernanceDetail(Actor(c), id, t)));
        api.MapPatch("/change-governance/{id:guid}", async (string id, JsonElement body, HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.ChangeGovernanceAction(Actor(c), id, "amend", body, t)));
        api.MapPost("/change-governance/{id:guid}/cab", async (string id, JsonElement body, HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.ChangeGovernanceAction(Actor(c), id, "cab", body, t)));
        api.MapPost("/change-governance/{id:guid}/reviews", async (string id, JsonElement body, HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.ChangeGovernanceAction(Actor(c), id, "review", body, t)));
    }
}

public sealed partial class MySqlStore
{
    public async Task<object> ChangeConfiguration(string actor, CancellationToken ct)
    {
        var access = await ChangeActor(actor, ct);
        var groups = await FQuery("SELECT g.*,COALESCE((SELECT JSON_ARRAYAGG(m.user_id) FROM cab_members m WHERE m.group_id=g.id),JSON_ARRAY()) AS member_ids FROM cab_groups g WHERE (@admin=1 OR g.active=1) ORDER BY name", ct, ("admin", access.Admin));
        if (!access.Admin)
            groups = groups.Select(g => new Dictionary<string, object?> { { "id", g["id"] }, { "name", g["name"] }, { "quorum", g["quorum"] } }).ToList();
        var templates = await FQuery("SELECT * FROM change_templates WHERE @admin=1 OR active=1 ORDER BY name", ct, ("admin", access.Admin));
        var blackouts = await FQuery("SELECT b.*,c.name AS client_name,s.name AS site_name FROM change_blackouts b LEFT JOIN clients c ON c.id=b.client_id LEFT JOIN sites s ON s.id=b.site_id WHERE @admin=1 OR (b.active=1 AND (b.client_id IS NULL OR EXISTS(SELECT 1 FROM client_users cu WHERE cu.user_id=@actor AND cu.client_id=b.client_id))) ORDER BY starts_at DESC LIMIT 100", ct, ("admin", access.Admin), ("actor", actor));
        var policy = (await FQuery("SELECT * FROM change_governance_policy WHERE id=1", ct)).FirstOrDefault() ?? new Dictionary<string, object?> { { "id", 1 }, { "high_risk_cab", true }, { "minimum_approvals", 1 }, { "review_required", true } };
        return new
        {
            policy,
            groups,
            templates,
            blackouts
        };
    }
    public async Task<object> SaveChangeConfiguration(string actor, string kind, string? id, JsonElement b, CancellationToken ct)
    {
        await RequireAdmin(actor, ct);
        await using var c = CreateConnection();
        await c.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await FRows(c, tx, "SELECT id FROM change_governance_policy WHERE id=1 FOR UPDATE", ct);
        var entity = id ?? Guid.NewGuid().ToString("D");
        if (kind == "policy")
        {
            var minimum = JInt(b, "minimumApprovals");
            if (minimum is < 1 or > 10)
                throw new ArgumentException("Minimum approvals must be between 1 and 10.");
            await FExecute(c, tx, "INSERT INTO change_governance_policy(id,high_risk_cab,minimum_approvals,review_required) VALUES(1,@cab,@minimum,@review) ON DUPLICATE KEY UPDATE high_risk_cab=@cab,minimum_approvals=@minimum,review_required=@review", ct, ("cab", JBool(b, "highRiskCab")), ("minimum", minimum), ("review", JBool(b, "reviewRequired")));
            entity = "1";
        }
        else
        {
            var table = kind switch
            {
                "groups" => "cab_groups",
                "templates" => "change_templates",
                "blackouts" => "change_blackouts",
                _ => throw new ArgumentException("Unknown governance resource.")
            };
            if (id is not null && (await FRows(c, tx, $"SELECT id FROM {table} WHERE id=@id FOR UPDATE", ct, ("id", id))).Count == 0)
                throw new KeyNotFoundException();
            var name = FText(JString(b, "name"), "Name", 2, kind == "templates" ? 150 : kind == "groups" ? 100 : 200);
            if (kind == "groups")
            {
                var quorum = JInt(b, "quorum");
                var members = JArray(b, "memberIds").Select(x => FGuid(x.GetString(), "CAB member")).ToArray();
                if (quorum < 1 || quorum > 50 || members.Length > 50 || members.Distinct().Count() != members.Length || members.Length < quorum)
                    throw new ArgumentException("Choose unique CAB members and a quorum within the member count.");
                foreach (var member in members)
                if ((await FRows(c, tx, "SELECT u.id FROM users u WHERE id=@id AND active=1 AND (change_access<>'NONE' OR EXISTS(SELECT 1 FROM user_roles r WHERE r.user_id=u.id AND r.role_code='ADMIN')) FOR SHARE", ct, ("id", member))).Count == 0)
                    throw new ArgumentException("Choose active CAB members with Change Management access.");
                await FExecute(c, tx, id is null ? "INSERT INTO cab_groups(id,name,quorum,active) VALUES(@id,@name,@quorum,@active)" : "UPDATE cab_groups SET name=@name,quorum=@quorum,active=@active WHERE id=@id", ct, ("id", entity), ("name", name), ("quorum", quorum), ("active", JBool(b, "active", true)));
                await FExecute(c, tx, "DELETE FROM cab_members WHERE group_id=@id", ct, ("id", entity));
                foreach (var member in members)
                    await FExecute(c, tx, "INSERT INTO cab_members(group_id,user_id) VALUES(@id,@member)", ct, ("id", entity), ("member", member));
            }
            else if (kind == "templates")
            {
                var category = JChoice(b, "category", ChangeCategories);
                var description = FText(JString(b, "description"), "Description", 3, 10000);
                var implementation = FText(JString(b, "implementationPlan"), "Implementation plan", 3, 10000);
                var test = FText(JString(b, "testPlan"), "Test plan", 3, 10000);
                var rollback = FText(JString(b, "rollbackPlan"), "Rollback plan", 3, 10000);
                await FExecute(c, tx, id is null ? "INSERT INTO change_templates(id,name,category,description,implementation_plan,test_plan,rollback_plan,active,created_by_user_id) VALUES(@id,@name,@category,@description,@implementation,@test,@rollback,@active,@actor)" : "UPDATE change_templates SET name=@name,category=@category,description=@description,implementation_plan=@implementation,test_plan=@test,rollback_plan=@rollback,active=@active,updated_at=UTC_TIMESTAMP(6) WHERE id=@id", ct, ("id", entity), ("name", name), ("category", category), ("description", description), ("implementation", implementation), ("test", test), ("rollback", rollback), ("active", JBool(b, "active", true)), ("actor", actor));
            }
            else
            {
                var start = JDate(b, "startsAt");
                var end = JDate(b, "endsAt");
                if (start is null || end is null || end <= start)
                    throw new ArgumentException("Choose a valid blackout start and end time.");
                var client = JOptional(b, "clientId");
                var site = JOptional(b, "siteId");
                if (client is not null && (await FRows(c, tx, "SELECT id FROM clients WHERE id=@id AND status='ACTIVE' FOR SHARE", ct, ("id", FGuid(client, "client")))).Count == 0)
                    throw new ArgumentException("Choose an active client.");
                if (site is not null && (client is null || (await FRows(c, tx, "SELECT id FROM sites WHERE id=@id AND client_id=@client AND active=1 FOR SHARE", ct, ("id", FGuid(site, "site")), ("client", client))).Count == 0))
                    throw new ArgumentException("Choose a site belonging to the selected client.");
                await FExecute(c, tx, id is null ? "INSERT INTO change_blackouts(id,name,client_id,site_id,starts_at,ends_at,active,created_by_user_id) VALUES(@id,@name,@client,@site,@start,@end,@active,@actor)" : "UPDATE change_blackouts SET name=@name,client_id=@client,site_id=@site,starts_at=@start,ends_at=@end,active=@active WHERE id=@id", ct, ("id", entity), ("name", name), ("client", client), ("site", site), ("start", start), ("end", end), ("active", JBool(b, "active", true)), ("actor", actor));
            }
        }
        await AddAudit(c, tx, actor, "CHANGE_GOVERNANCE." + kind.ToUpperInvariant() + "_SAVED", "CHANGE_GOVERNANCE", entity, new
        {
            kind,
            id = entity
        }, ct);
        await tx.CommitAsync(ct);
        return new
        {
            id = entity
        };
    }
    public async Task<object> ChangeGovernanceDetail(string actor, string id, CancellationToken ct)
    {
        var change = await VisibleChange(actor, id, ct);
        await using var c = CreateConnection();
        await c.OpenAsync(ct);
        var readiness = await ChangeReadiness(c, null, change, ct);
        var conflicts = await ChangeConflicts(c, null, change, ct);
        var visible = new List<Dictionary<string, object?>>();
        var restrictedConflictCount = 0;
        foreach (var other in conflicts.Changes)
        {
            try
            {
                await VisibleChange(actor, (string)other["id"]!, ct);
                visible.Add(other);
            }
            catch (KeyNotFoundException) { restrictedConflictCount++; }
        }
        var reviews = await FRows(c, null, "SELECT r.*,u.display_name AS author_name FROM change_reviews r JOIN users u ON u.id=r.actor_user_id WHERE change_id=@id ORDER BY created_at DESC", ct, ("id", id));
        return new
        {
            readiness,
            conflicts = new
            {
                changes = visible,
                blackouts = conflicts.Blackouts,
                restrictedConflictCount
            },
            reviews
        };
    }
    public async Task<object> ChangeGovernanceAction(string actor, string id, string action, JsonElement b, CancellationToken ct)
    {
        var access = await ChangeActor(actor, ct);
        if (!access.Coordinator)
            throw new UnauthorizedAccessException();
        await VisibleChange(actor, id, ct);
        await using var c = CreateConnection();
        await c.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await FRows(c, tx, "SELECT id FROM change_governance_policy WHERE id=1 FOR UPDATE", ct);
        var change = (await FRows(c, tx, "SELECT * FROM changes WHERE id=@id FOR UPDATE", ct, ("id", id)))[0];
        var state = (string)change["status"]!;
        var entity = Guid.NewGuid().ToString("D");
        if (action == "amend")
        {
            if (state is "IN_PROGRESS" or "VALIDATION" or "COMPLETED" or "CLOSED" or "FAILED" or "ROLLED_BACK" or "CANCELLED")
                throw new ArgumentException("Revise the change before implementation starts.");
            if (JInt(b, "revision") != FInt(change["revision"]))
                throw new InvalidOperationException("This change was updated. Reload before revising.");
            var reason = FText(JString(b, "reason"), "Revision reason", 10, 2000);
            var start = b.TryGetProperty("plannedStart", out _) ? JDate(b, "plannedStart") : (DateTime?)change["planned_start"];
            var end = b.TryGetProperty("plannedEnd", out _) ? JDate(b, "plannedEnd") : (DateTime?)change["planned_end"];
            if ((start is null) != (end is null) || end <= start)
                throw new ArgumentException("Choose a valid planned start and end time.");
            var risk = JOptional(b, "risk") ?? (string)change["risk"]!;
            if (!new[] { "LOW", "MEDIUM", "HIGH", "CRITICAL" }.Contains(risk))
                throw new ArgumentException("Choose a valid risk.");
            var resources = b.TryGetProperty("affectedResources", out _) ? JArray(b, "affectedResources").Select(x => FText(x.GetString(), "Affected resource", 1, 150)).Distinct().ToArray() : FStrings(change["affected_resources"]);
            if (resources.Length > 50)
                throw new ArgumentException("Choose at most 50 affected resources.");
            var implementation = FText(JOptional(b, "implementationPlan") ?? (string)change["implementation_plan"]!, "Implementation plan", 2, 10000);
            var test = FText(JOptional(b, "testPlan") ?? (string)change["test_plan"]!, "Test plan", 2, 10000);
            var rollback = FText(JOptional(b, "rollbackPlan") ?? (string)change["rollback_plan"]!, "Rollback plan", 2, 10000);
            await FExecute(c, tx, "UPDATE changes SET planned_start=@start,planned_end=@end,risk=@risk,implementation_plan=@implementation,test_plan=@test,rollback_plan=@rollback,affected_resources=CAST(@resources AS JSON),revision=revision+1,cab_group_id=NULL,cab_quorum=0,status=IF(status='DRAFT','DRAFT','ASSESSMENT'),updated_at=UTC_TIMESTAMP(6) WHERE id=@id", ct, ("id", id), ("start", start), ("end", end), ("risk", risk), ("implementation", implementation), ("test", test), ("rollback", rollback), ("resources", FJson(resources)));
            await ChangeEvent(c, tx, actor, id, "CHANGE.REVISED", new
            {
                previousRevision = change["revision"],
                reason
            }, ct);
            await tx.CommitAsync(ct);
            return new
            {
                id,
                revision = FInt(change["revision"]) + 1
            };
        }
        if (action == "cab")
        {
            if (state is not ("DRAFT" or "SUBMITTED" or "ASSESSMENT") || change["cab_group_id"] is not null)
                throw new ArgumentException("Request CAB in a new assessment round before sending for approval.");
            var groupId = FGuid(JString(b, "groupId"), "CAB group");
            var group = (await FRows(c, tx, "SELECT * FROM cab_groups WHERE id=@id AND active=1 FOR SHARE", ct, ("id", groupId))).FirstOrDefault() ?? throw new ArgumentException("Choose an active CAB group.");
            var members = await FRows(c, tx, "SELECT u.id FROM cab_members m JOIN users u ON u.id=m.user_id WHERE m.group_id=@group AND u.active=1 AND u.id<>@owner AND u.id<>@requester AND (EXISTS(SELECT 1 FROM user_roles r WHERE r.user_id=u.id AND r.role_code='ADMIN') OR (u.change_access<>'NONE' AND EXISTS(SELECT 1 FROM client_users cu WHERE cu.user_id=u.id AND cu.client_id=@client)))", ct, ("group", groupId), ("owner", change["owner_user_id"]), ("requester", change["requested_by_user_id"]), ("client", change["client_id"]));
            if (members.Count < FInt(group["quorum"]))
                throw new ArgumentException("Not enough independent CAB members have access to this client.");
            foreach (var member in members)
            {
                var vote = Guid.NewGuid().ToString("D");
                await FExecute(c, tx, "INSERT INTO change_approvals(id,change_id,approver_user_id,required,sequence,revision,cab_group_id) VALUES(@vote,@id,@user,0,1,@revision,@group)", ct, ("vote", vote), ("id", id), ("user", member["id"]), ("revision", change["revision"]), ("group", groupId));
                await NotifyChange(c, tx, (string)member["id"]!, id, "CHANGE_APPROVAL_REQUESTED", $"CAB review requested: {change["change_number"]}", (string)change["title"]!, "cab:" + vote, ct);
            }
            await FExecute(c, tx, "UPDATE changes SET cab_group_id=@group,cab_quorum=@quorum,updated_at=UTC_TIMESTAMP(6) WHERE id=@id", ct, ("id", id), ("group", groupId), ("quorum", group["quorum"]));
            await ChangeEvent(c, tx, actor, id, "CAB.REVIEW_REQUESTED", new
            {
                groupId,
                quorum = group["quorum"],
                revision = change["revision"]
            }, ct);
            await tx.CommitAsync(ct);
            return new
            {
                id
            };
        }
        if (action == "review")
        {
            if (state is not ("COMPLETED" or "FAILED" or "ROLLED_BACK"))
                throw new ArgumentException("Complete implementation before recording its review.");
            var outcome = JChoice(b, "outcome", "SUCCESS", "PARTIAL", "FAILED", "ROLLED_BACK");
            var summary = FText(JString(b, "summary"), "Review summary", 10, 10000);
            var followUp = FText(JString(b, "followUp"), "Follow-up", 2, 10000);
            await FExecute(c, tx, "INSERT INTO change_reviews(id,change_id,revision,outcome,summary,follow_up,actor_user_id) VALUES(@review,@id,@revision,@outcome,@summary,@followup,@actor)", ct, ("review", entity), ("id", id), ("revision", change["revision"]), ("outcome", outcome), ("summary", summary), ("followup", followUp), ("actor", actor));
            await ChangeEvent(c, tx, actor, id, "CHANGE.REVIEW_RECORDED", new
            {
                reviewId = entity,
                outcome,
                revision = change["revision"]
            }, ct);
            await tx.CommitAsync(ct);
            return new
            {
                id = entity
            };
        }
        throw new ArgumentException("Unknown governance action.");
    }
}
