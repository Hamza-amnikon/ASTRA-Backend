using System.Text.Json;
using MySqlConnector;

public sealed record ApprovalReadiness(bool Ready, List<string> Reasons, int Approved, int CabApproved, Dictionary<string, object?> Policy);
public sealed partial class MySqlStore
{
    private static readonly Dictionary<string, string[]> ChangeTransitions = new() { ["DRAFT"] = ["SUBMITTED", "CANCELLED"], ["SUBMITTED"] = ["ASSESSMENT", "CANCELLED"], ["ASSESSMENT"] = ["AWAITING_APPROVAL", "CANCELLED"], ["AWAITING_APPROVAL"] = ["ASSESSMENT", "CANCELLED"], ["APPROVED"] = ["SCHEDULED", "ASSESSMENT", "CANCELLED"], ["SCHEDULED"] = ["IN_PROGRESS", "ASSESSMENT", "CANCELLED"], ["IN_PROGRESS"] = ["VALIDATION", "FAILED", "ROLLED_BACK"], ["VALIDATION"] = ["COMPLETED", "FAILED", "ROLLED_BACK"], ["COMPLETED"] = ["CLOSED", "ROLLED_BACK"], ["FAILED"] = ["CLOSED"], ["ROLLED_BACK"] = ["CLOSED"] };
    private static async Task<ApprovalReadiness> ChangeReadiness(MySqlConnection c, MySqlTransaction? tx, Dictionary<string, object?> change, CancellationToken ct)
    {
        var policy = (await FRows(c, tx, "SELECT * FROM change_governance_policy WHERE id=1", ct)).FirstOrDefault() ?? new Dictionary<string, object?> { { "high_risk_cab", true }, { "minimum_approvals", 1 }, { "review_required", true } };
        var votes = await FRows(c, tx, "SELECT a.*,u.active FROM change_approvals a LEFT JOIN users u ON u.id=a.decision_by_user_id WHERE a.change_id=@id AND a.revision=@revision", ct, ("id", change["id"]), ("revision", change["revision"]));
        var approved = votes.Where(v => (string?)v["status"] == "APPROVED" && FBool(v["active"]) && !Equals(v["decision_by_user_id"], change["requested_by_user_id"]) && !Equals(v["decision_by_user_id"], change["owner_user_id"])).ToList();
        var independent = approved.Select(v => v["decision_by_user_id"]).Distinct().Count();
        var cab = approved.Where(v => change["cab_group_id"] is not null && Equals(v["cab_group_id"], change["cab_group_id"]) && Equals(v["approver_user_id"], v["decision_by_user_id"])).Select(v => v["decision_by_user_id"]).Distinct().Count();
        var reasons = new List<string>();
        if (independent < FInt(policy["minimum_approvals"]))
            reasons.Add($"At least {policy["minimum_approvals"]} independent approval(s) are required.");
        if (votes.Any(v => FBool(v["required"]) && !approved.Contains(v)))
            reasons.Add("All required approvals must be approved.");
        if (votes.Any(v => (string?)v["status"] is "REJECTED" or "CHANGES_REQUESTED"))
            reasons.Add("A rejection or request for changes requires a new review round.");
        if (FBool(policy["high_risk_cab"]) && (string?)change["risk"] is "HIGH" or "CRITICAL" && (change["cab_group_id"] is null || FInt(change["cab_quorum"]) == 0))
            reasons.Add("High or critical risk requires a CAB group.");
        if (change["cab_group_id"] is not null && cab < FInt(change["cab_quorum"]))
            reasons.Add($"CAB requires {change["cab_quorum"]} independent member approval(s).");
        return new(reasons.Count == 0, reasons, independent, cab, policy);
    }
    private static async Task<(List<Dictionary<string, object?>> Changes, List<Dictionary<string, object?>> Blackouts)> ChangeConflicts(MySqlConnection c, MySqlTransaction? tx, Dictionary<string, object?> change, CancellationToken ct)
    {
        if (change["planned_start"] is null || change["planned_end"] is null)
            return ([], []);
        var args = new (string, object?)[] { ("id", change["id"]), ("client", change["client_id"]), ("site", change["site_id"]), ("start", change["planned_start"]), ("end", change["planned_end"]), ("owner", change["owner_user_id"]), ("resources", change["affected_resources"] is JsonElement j ? j.GetRawText() : Convert.ToString(change["affected_resources"]) ?? "[]") };
        var conflicts = await FRows(c, tx, "SELECT id,change_number,title,planned_start,planned_end,'Overlapping site, owner or affected resource' AS reason FROM changes WHERE id<>@id AND client_id=@client AND status IN ('APPROVED','SCHEDULED','IN_PROGRESS','VALIDATION') AND planned_start<@end AND planned_end>@start AND (site_id IS NULL OR @site IS NULL OR site_id=@site OR owner_user_id=@owner OR JSON_OVERLAPS(affected_resources,CAST(@resources AS JSON))) ORDER BY planned_start LIMIT 100", ct, args);
        var blackouts = await FRows(c, tx, "SELECT id,name,starts_at,ends_at FROM change_blackouts WHERE active=1 AND (client_id IS NULL OR client_id=@client) AND (site_id IS NULL OR @site IS NULL OR site_id=@site) AND starts_at<@end AND ends_at>@start ORDER BY starts_at LIMIT 100", ct, args);
        return (conflicts, blackouts);
    }
    private static async Task NotifyChange(MySqlConnection c, MySqlTransaction tx, string user, string change, string type, string title, string body, string key, CancellationToken ct)
    {
        await FExecute(c, tx, "INSERT IGNORE INTO notifications(user_id,change_id,type,title,body,event_key) VALUES(@user,@change,@type,@title,@body,@key)", ct, ("user", user), ("change", change), ("type", type), ("title", title), ("body", body), ("key", key));
    }
    public async Task<object> ChangeAction(string actor, string id, string action, string? subId, JsonElement b, CancellationToken ct)
    {
        var access = await ChangeActor(actor, ct);
        await VisibleChange(actor, id, ct);
        await using var c = CreateConnection();
        await c.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        // This policy row serializes schedule edits and scheduling checks across changes.
        await FRows(c, tx, "SELECT id FROM change_governance_policy WHERE id=1 FOR UPDATE", ct);
        var change = (await FRows(c, tx, "SELECT c.* FROM changes c WHERE id=@id AND " + ChangeScope() + " FOR UPDATE", ct, ("id", id), ("actor", actor), ("admin", access.Admin), ("manager", access.Manager))).FirstOrDefault() ?? throw new KeyNotFoundException();
        var state = (string)change["status"]!;
        var resultId = Guid.NewGuid().ToString("D");
        object result = new
        {
            id = resultId
        };
        if (action is "teams" or "tasks" or "approvals" or "tickets" or "stakeholders" or "communications" && !access.Coordinator)
            throw new UnauthorizedAccessException();
        switch (action)
        {
            case "status":
                var next = JChoice(b, "status", ChangeStatuses);
                if (!access.Coordinator && !(Equals(change["owner_user_id"], actor) && state == "DRAFT" && next is "SUBMITTED" or "CANCELLED"))
                    throw new UnauthorizedAccessException();
                if (!ChangeTransitions.GetValueOrDefault(state, []).Contains(next))
                    throw new ArgumentException($"Cannot move from {state} to {next}.");
                if (next is "SCHEDULED" or "IN_PROGRESS")
                {
                    var ready = await ChangeReadiness(c, tx, change, ct);
                    if (!ready.Ready)
                        throw new ArgumentException(string.Join(" ", ready.Reasons));
                    if (change["planned_start"] is not DateTime start || change["planned_end"] is not DateTime end || end <= start)
                        throw new ArgumentException("Choose a valid planned start and end time.");
                    var conflicts = await ChangeConflicts(c, tx, change, ct);
                    if (conflicts.Changes.Count > 0 || conflicts.Blackouts.Count > 0)
                    {
                        if (!access.Manager || JString(b, "overrideReason").Trim().Length < 10)
                            throw new ArgumentException("Schedule conflict or blackout found. Reschedule or ask a Change Manager to record an override reason of at least 10 characters.");
                        await ChangeEvent(c, tx, actor, id, "CHANGE.CONFLICT_OVERRIDE", new
                        {
                            reason = JString(b, "overrideReason"),
                            revision = change["revision"]
                        }, ct);
                    }
                }
                if (next == "AWAITING_APPROVAL" && (await FRows(c, tx, "SELECT id FROM change_approvals WHERE change_id=@id AND revision=@revision LIMIT 1", ct, ("id", id), ("revision", change["revision"]))).Count == 0)
                    throw new ArgumentException("Add independent approvers or request CAB review first.");
                if (next == "VALIDATION" && (await FRows(c, tx, "SELECT id FROM change_tasks WHERE change_id=@id AND status NOT IN ('COMPLETED','WAIVED') LIMIT 1", ct, ("id", id))).Count > 0)
                    throw new ArgumentException("Complete or waive all tasks before validation.");
                if (next == "CLOSED")
                {
                    var policy = (await ChangeReadiness(c, tx, change, ct)).Policy;
                    if ((FBool(policy["review_required"]) || (string?)change["change_type"] == "EMERGENCY") && (await FRows(c, tx, "SELECT id FROM change_reviews WHERE change_id=@id AND revision=@revision LIMIT 1", ct, ("id", id), ("revision", change["revision"]))).Count == 0)
                        throw new ArgumentException("Record a post-implementation review before closing.");
                }
                var revise = next == "ASSESSMENT" && state is "APPROVED" or "SCHEDULED" or "AWAITING_APPROVAL";
                await FExecute(c, tx, "UPDATE changes SET status=@status,revision=revision+@revise,cab_group_id=IF(@revise=1,NULL,cab_group_id),cab_quorum=IF(@revise=1,0,cab_quorum),actual_start=IF(@status='IN_PROGRESS',COALESCE(actual_start,UTC_TIMESTAMP(6)),actual_start),actual_end=IF(@status IN ('COMPLETED','CLOSED','FAILED','ROLLED_BACK'),COALESCE(actual_end,UTC_TIMESTAMP(6)),actual_end),updated_at=UTC_TIMESTAMP(6) WHERE id=@id", ct, ("id", id), ("status", next), ("revise", revise));
                result = new
                {
                    id,
                    status = next
                };
                break;
            case "teams":
                var team = FGuid(JString(b, "departmentId"), "department");
                var teamRole = JChoice(b, "role", "LEAD", "IMPLEMENTER", "REVIEWER");
                if ((await FRows(c, tx, "SELECT id FROM departments WHERE id=@id AND active=1 FOR SHARE", ct, ("id", team))).Count == 0)
                    throw new ArgumentException("Choose an active department.");
                await FExecute(c, tx, "INSERT INTO change_teams(change_id,department_id,role,status) VALUES(@id,@team,@role,'ACTIVE') ON DUPLICATE KEY UPDATE role=@role,status='ACTIVE'", ct, ("id", id), ("team", team), ("role", teamRole));
                result = new
                {
                    id
                };
                break;
            case "tasks":
                var title = FText(JString(b, "title"), "Task title", 2, 300);
                var instructions = FText(JString(b, "instructions"), "Instructions", 0, 5000);
                var assigned = JOptional(b, "assignedUserId");
                var dept = JOptional(b, "departmentId");
                if (state is "COMPLETED" or "CLOSED" or "FAILED" or "ROLLED_BACK" or "CANCELLED")
                    throw new ArgumentException("Tasks cannot be added after implementation has ended.");
                if (assigned is not null)
                    await ValidateChangeStaff(c, tx, assigned, (string)change["client_id"]!, ct, (string?)change["site_id"]);
                if (dept is not null && (await FRows(c, tx, "SELECT id FROM departments WHERE id=@id AND active=1 FOR SHARE", ct, ("id", FGuid(dept, "department")))).Count == 0)
                    throw new ArgumentException("Choose an active department.");
                await FExecute(c, tx, "INSERT INTO change_tasks(id,change_id,department_id,assigned_user_id,title,instructions,due_at) VALUES(@task,@id,@dept,@assigned,@title,@instructions,@due)", ct, ("task", resultId), ("id", id), ("dept", dept), ("assigned", assigned), ("title", title), ("instructions", instructions), ("due", JDate(b, "dueAt")));
                if (assigned is not null)
                    await NotifyChange(c, tx, assigned, id, "CHANGE_TASK_ASSIGNED", $"Task assigned on {change["change_number"]}", title, "change-task:" + resultId, ct);
                break;
            case "task-update":
                var task = (await FRows(c, tx, "SELECT * FROM change_tasks WHERE id=@task AND change_id=@id FOR UPDATE", ct, ("task", subId), ("id", id))).FirstOrDefault() ?? throw new KeyNotFoundException();
                if (!access.Coordinator && !Equals(task["assigned_user_id"], actor))
                    throw new UnauthorizedAccessException();
                if (state is "COMPLETED" or "CLOSED" or "FAILED" or "ROLLED_BACK" or "CANCELLED")
                    throw new ArgumentException("Tasks cannot be changed after implementation has ended.");
                var taskStatus = JChoice(b, "status", "TODO", "IN_PROGRESS", "BLOCKED", "COMPLETED", "WAIVED");
                var note = FText(JString(b, "updateNote"), "Task note", 0, 5000);
                if (taskStatus == "WAIVED" && (!access.Coordinator || note.Length < 10))
                    throw new ArgumentException("Only a coordinator can waive a task with a reason of at least 10 characters.");
                await FExecute(c, tx, "UPDATE change_tasks SET status=@status,update_note=@note,completed_at=IF(@status='COMPLETED',UTC_TIMESTAMP(6),NULL) WHERE id=@task AND change_id=@id", ct, ("task", subId), ("id", id), ("status", taskStatus), ("note", note));
                result = new
                {
                    id = subId
                };
                break;
            case "approvals":
                if (state is not ("DRAFT" or "SUBMITTED" or "ASSESSMENT"))
                    throw new ArgumentException("Add reviewers during assessment before requesting approval.");
                var approver = JOptional(b, "approverUserId");
                var department = JOptional(b, "departmentId");
                if ((approver is null) == (department is null))
                    throw new ArgumentException("Choose one approver or department.");
                if (approver is not null)
                    FGuid(approver, "approver");
                if (department is not null)
                    FGuid(department, "department");
                var eligible = await FRows(c, tx, "SELECT u.id FROM users u WHERE active=1 AND u.id<>@owner AND u.id<>@requester AND (u.id=@approver OR EXISTS(SELECT 1 FROM department_users du WHERE du.user_id=u.id AND du.department_id=@department)) AND (EXISTS(SELECT 1 FROM user_roles ur WHERE ur.user_id=u.id AND ur.role_code='ADMIN') OR (u.change_access<>'NONE' AND EXISTS(SELECT 1 FROM client_users cu WHERE cu.user_id=u.id AND cu.client_id=@client))) FOR SHARE", ct, ("owner", change["owner_user_id"]), ("requester", change["requested_by_user_id"]), ("approver", approver), ("department", department), ("client", change["client_id"]));
                if (eligible.Count == 0)
                    throw new ArgumentException("Choose an independent reviewer with access to this client.");
                var sequence = JInt(b, "sequence", 1);
                if (sequence < 1 || sequence > 100)
                    throw new ArgumentException("Approval sequence must be between 1 and 100.");
                await FExecute(c, tx, "INSERT INTO change_approvals(id,change_id,approver_user_id,department_id,sequence,required,revision) VALUES(@vote,@id,@approver,@department,@sequence,@required,@revision)", ct, ("vote", resultId), ("id", id), ("approver", approver), ("department", department), ("sequence", sequence), ("required", JBool(b, "required", true)), ("revision", change["revision"]));
                foreach (var member in eligible)
                    await NotifyChange(c, tx, (string)member["id"]!, id, "CHANGE_APPROVAL_REQUESTED", $"Review requested: {change["change_number"]}", (string)change["title"]!, "approval:" + resultId + ":" + member["id"], ct);
                break;
            case "decision":
                if (state != "AWAITING_APPROVAL")
                    throw new ArgumentException("Decisions are only allowed while awaiting approval.");
                if (Equals(change["owner_user_id"], actor) || Equals(change["requested_by_user_id"], actor))
                    throw new ArgumentException("Requester and owner cannot approve their own change.");
                var vote = (await FRows(c, tx, "SELECT * FROM change_approvals WHERE id=@vote AND change_id=@id AND revision=@revision AND status='PENDING' FOR UPDATE", ct, ("vote", subId), ("id", id), ("revision", change["revision"]))).FirstOrDefault() ?? throw new InvalidOperationException("This review is no longer pending in the current round.");
                var departmentMember = vote["cab_group_id"] is null && vote["department_id"] is not null && (await FRows(c, tx, "SELECT 1 FROM department_users WHERE user_id=@actor AND department_id=@department", ct, ("actor", actor), ("department", vote["department_id"]))).Count > 0;
                if (!Equals(vote["approver_user_id"], actor) && !departmentMember)
                    throw new UnauthorizedAccessException();
                if ((await FRows(c, tx, "SELECT id FROM change_approvals WHERE change_id=@id AND revision=@revision AND required=1 AND sequence<@sequence AND status<>'APPROVED' LIMIT 1", ct, ("id", id), ("revision", change["revision"]), ("sequence", vote["sequence"]))).Count > 0)
                    throw new ArgumentException("Earlier required approval stages must finish first.");
                var decision = JChoice(b, "status", "APPROVED", "REJECTED", "CHANGES_REQUESTED");
                var decisionNote = FText(JString(b, "note"), "Decision note", 2, 2000);
                await FExecute(c, tx, "UPDATE change_approvals SET status=@status,decision_by_user_id=@actor,decision_note=@note,decided_at=UTC_TIMESTAMP(6) WHERE id=@vote", ct, ("status", decision), ("actor", actor), ("note", decisionNote), ("vote", subId));
                if (decision != "APPROVED")
                    await FExecute(c, tx, "UPDATE changes SET status='ASSESSMENT',revision=revision+1,cab_group_id=NULL,cab_quorum=0,updated_at=UTC_TIMESTAMP(6) WHERE id=@id", ct, ("id", id));
                else if ((await ChangeReadiness(c, tx, change, ct)).Ready)
                    await FExecute(c, tx, "UPDATE changes SET status='APPROVED',updated_at=UTC_TIMESTAMP(6) WHERE id=@id", ct, ("id", id));
                result = new
                {
                    id = subId
                };
                break;
            case "updates":
                var updateType = JChoice(b, "updateType", "INTERNAL", "IMPLEMENTATION", "VALIDATION", "CLIENT_COMMUNICATION");
                var body = FText(JString(b, "body"), "Update", 2, 10000);
                await FExecute(c, tx, "INSERT INTO change_updates(id,change_id,author_user_id,update_type,body) VALUES(@update,@id,@actor,@type,@body)", ct, ("update", resultId), ("id", id), ("actor", actor), ("type", updateType), ("body", body));
                break;
            case "tickets":
                var ticket = FGuid(JString(b, "ticketId"), "ticket");
                if ((await FRows(c, tx, "SELECT t.id FROM tickets t WHERE t.id=@ticket AND t.client_id=@client AND " + FScope("t") + " FOR SHARE", ct, ("ticket", ticket), ("client", change["client_id"]), ("actor", actor), ("admin", access.Admin))).Count == 0)
                    throw new ArgumentException("Choose an accessible ticket belonging to the same client.");
                await FExecute(c, tx, "INSERT INTO change_ticket_links(change_id,ticket_id,linked_by_user_id) VALUES(@id,@ticket,@actor)", ct, ("id", id), ("ticket", ticket), ("actor", actor));
                result = new
                {
                    id
                };
                break;
            case "stakeholders":
                result = await AddChangeStakeholder(c, tx, actor, change, b, ct);
                break;
            case "attachments":
                result = await SaveChangeAttachment(c, tx, actor, id, b, ct);
                break;
            case "communications":
                var recipients = JArray(b, "recipientEmails").Select(x => x.GetString()?.Trim().ToLowerInvariant() ?? "").Distinct().ToArray();
                if (recipients.Length is < 1 or > 100 || recipients.Any(x => !System.Net.Mail.MailAddress.TryCreate(x, out var address) || address.Address != x))
                    throw new ArgumentException("Enter valid recipient email addresses.");
                await FExecute(c, tx, "INSERT INTO change_communications(id,change_id,recipient_emails,subject,body,include_client,created_by_user_id) VALUES(@communication,@id,CAST(@recipients AS JSON),@subject,@body,@include,@actor)", ct, ("communication", resultId), ("id", id), ("recipients", FJson(recipients)), ("subject", FText(JString(b, "subject"), "Subject", 2, 300)), ("body", FText(JString(b, "body"), "Message", 2, 20000)), ("include", JBool(b, "includeClient")), ("actor", actor));
                break;
            default:
                throw new ArgumentException("Unknown change action.");
        }
        await ChangeEvent(c, tx, actor, id, "CHANGE." + action.ToUpperInvariant().Replace('-', '_'), new
        {
            recordId = result,
            revision = change["revision"],
            beforeStatus = state,
            afterStatus = action == "status" ? JString(b, "status") : null,
            note = JOptional(b, "note") ?? JOptional(b, "updateNote")
        }, ct);
        await tx.CommitAsync(ct);
        return result;
    }
}
