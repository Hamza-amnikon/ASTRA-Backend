using System.Security.Cryptography;
using System.Text.Json;
using Astra.Shared.Data;
using Astra.Shared.Security;
using MySqlConnector;

namespace TicketService.API.Services;

public sealed class TicketWriteService(Database database)
{
    private static readonly string[] StaffRoles = ["INTERN", "TECHNICIAN", "TEAM_LEAD", "MANAGER", "ADMIN"];
    private static readonly HashSet<string> AttachmentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf", "application/zip", "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "image/gif", "image/jpeg", "image/png", "text/csv", "text/plain"
    };
    private const int MaxAttachmentBytes = 5 * 1024 * 1024;
    private const string InternalClient = "00000000-0000-4000-8000-000000000132";

    private static void RequireStaff(Actor actor)
    {
        if (!actor.Roles.Overlaps(StaffRoles)) throw new ApiFault(403, "Service-desk access required.");
    }

    private static bool Wide(Actor actor) => actor.Roles.Overlaps(["TEAM_LEAD", "MANAGER", "ADMIN"]);
    private static string AttachmentAccess() => "(@scopeAdmin=1 OR (t.client_id IN(SELECT `value` FROM JSON_TABLE(@scopeClients, '$[*]' COLUMNS(`value` VARCHAR(100) PATH '$')) AS json_values) AND (@wide=1 OR t.assigned_user_id=@actor OR EXISTS(SELECT 1 FROM ticket_collaborators tc WHERE tc.ticket_id=t.id AND tc.user_id=@actor) OR (@team=1 AND (@scopeSiteCount=0 OR t.site_id IN(SELECT `value` FROM JSON_TABLE(@scopeSites, '$[*]' COLUMNS(`value` VARCHAR(100) PATH '$')) AS json_values))))))";
    private static string MutationAccess() => "(@scopeAdmin=1 OR (t.client_id IN(SELECT `value` FROM JSON_TABLE(@scopeClients, '$[*]' COLUMNS(`value` VARCHAR(100) PATH '$')) AS json_values) AND (@wide=1 OR t.assigned_user_id=@actor OR EXISTS(SELECT 1 FROM ticket_collaborators tc WHERE tc.ticket_id=t.id AND tc.user_id=@actor))))";

    private static async Task ValidateAssignedUserAsync(MySqlConnection connection, MySqlTransaction transaction, Guid userId, Guid clientId, Guid? siteId, CancellationToken cancellationToken)
    {
        var result = await Database.QueryAsync(connection, """
            SELECT u.id FROM users u JOIN user_roles r ON r.user_id=u.id
            WHERE u.id=@user AND u.active=1 AND r.role_code IN('INTERN','TECHNICIAN','TEAM_LEAD','MANAGER','ADMIN')
              AND (r.role_code='ADMIN' OR EXISTS(SELECT 1 FROM client_users cu WHERE cu.user_id=u.id AND cu.client_id=@client))
              AND (r.role_code='ADMIN' OR @site IS NULL OR NOT EXISTS(SELECT 1 FROM user_sites us WHERE us.user_id=u.id) OR EXISTS(SELECT 1 FROM user_sites us WHERE us.user_id=u.id AND us.site_id=@site)) LIMIT 1
            """, new Dictionary<string, object?> { ["user"] = userId, ["client"] = clientId, ["site"] = siteId }, transaction, cancellationToken);
        if (result.Count == 0) throw new ApiFault(400, "Assigned user is not active service-desk staff.");
    }

    private static async Task HistoryAsync(MySqlConnection connection, MySqlTransaction transaction, Guid ticketId, Guid actorId, string action, object? previous, object? next, CancellationToken cancellationToken)
    {
        await Database.ExecuteAsync(connection,
            "INSERT INTO ticket_history(ticket_id,actor_user_id,action,previous_value,new_value) VALUES(@ticket,@actor,@action,@previous,@next)",
            new Dictionary<string, object?>
            {
                ["ticket"] = ticketId,
                ["actor"] = actorId,
                ["action"] = action,
                ["previous"] = previous is null ? null : JsonSerializer.Serialize(previous, JsonSerializerOptions.Web),
                ["next"] = next is null ? null : JsonSerializer.Serialize(next, JsonSerializerOptions.Web)
            }, transaction, cancellationToken);
    }

    public async Task<object> CreateAsync(Actor actor, CreateTicketInput input, CancellationToken cancellationToken)
    {
        RequireStaff(actor);
        if (input.ClientId == Guid.Empty || input.RequesterId == Guid.Empty || input.CategoryId == Guid.Empty
            || input.Subject is null || input.Subject.Trim().Length is < 3 or > 300
            || input.Description is null || input.Description.Trim().Length is < 3 or > 20000
            || input.Source is null || input.Source.Length is < 1 or > 50 || !input.Source.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_')
            || input.Priority is not ("P1_CRITICAL" or "P2_HIGH" or "P3_MEDIUM" or "P4_LOW")
            || input.LocationText?.Length > 500 || input.SourceReferenceId?.Length > 500 || input.SourceConversationId?.Length > 500
            || input.SourceMessageId?.Length > 500 || input.SourceUrl?.Length > 2000 || input.ExternalThreadId?.Length > 500
            || (input.TagIds?.Length ?? 0) > 30 || (input.Attachments?.Length ?? 0) > 4)
            throw new ApiFault(400, "Invalid ticket details.");
        if (!actor.Roles.Contains("ADMIN") && !actor.ClientIds.Contains(input.ClientId)) throw new ApiFault(403, "Organization is not assigned.");
        if (input.SiteId.HasValue && !actor.Roles.Contains("ADMIN") && actor.SiteIds.Count > 0 && !actor.SiteIds.Contains(input.SiteId.Value)) throw new ApiFault(403, "Site is not assigned.");

        var attachments = new List<(TicketAttachmentInput Input, Guid Id, byte[] Content, string Hash)>();
        foreach (var attachment in input.Attachments ?? [])
        {
            if (attachment.FileName is null || attachment.FileName.Trim().Length is < 1 or > 255 || attachment.FileName.Contains('/') || attachment.FileName.Contains('\\')
                || attachment.ContentType is null || attachment.ContentType.Length > 200 || !AttachmentTypes.Contains(attachment.ContentType)
                || attachment.Visibility is not (null or "PUBLIC" or "INTERNAL")
                || string.IsNullOrEmpty(attachment.ContentBase64) || attachment.ContentBase64.Length > ((MaxAttachmentBytes + 2) / 3 * 4 + 8))
                throw new ApiFault(400, "Invalid ticket attachment.");
            byte[] bytes;
            try { bytes = Convert.FromBase64String(attachment.ContentBase64); }
            catch (FormatException) { throw new ApiFault(400, "Invalid ticket attachment data."); }
            if (bytes.Length is < 1 or > MaxAttachmentBytes) throw new ApiFault(400, "Ticket attachments must be no larger than 5 MB.");
            attachments.Add((attachment, Guid.NewGuid(), bytes, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()));
        }

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (MySqlTransaction)await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        var values = new Dictionary<string, object?> { ["client"] = input.ClientId, ["requester"] = input.RequesterId, ["site"] = input.SiteId, ["department"] = input.DepartmentId, ["category"] = input.CategoryId, ["source"] = input.Source, ["actor"] = actor.Id };
        var client = await Database.QueryAsync(connection, "SELECT id FROM clients WHERE id=@client AND status='ACTIVE' FOR UPDATE", values, transaction, cancellationToken);
        if (client.Count == 0) throw new ApiFault(400, "Invalid organization.");
        var requester = await Database.QueryAsync(connection, "SELECT id FROM contacts WHERE id=@requester AND client_id=@client AND active=1", values, transaction, cancellationToken);
        if (requester.Count == 0) throw new ApiFault(400, "Requester does not belong to the selected organization.");
        if (input.SiteId.HasValue)
        {
            var site = await Database.QueryAsync(connection, "SELECT id FROM sites WHERE id=@site AND client_id=@client AND active=1", values, transaction, cancellationToken);
            if (site.Count == 0) throw new ApiFault(400, "Site does not belong to the selected organization.");
        }
        var internalTicket = input.ClientId == Guid.Parse(InternalClient);
        if (internalTicket && !input.DepartmentId.HasValue) throw new ApiFault(400, "Internal tickets require a department.");
        if (input.DepartmentId.HasValue)
        {
            if (!internalTicket) throw new ApiFault(400, "Departments apply only to internal tickets.");
            var department = await Database.QueryAsync(connection, "SELECT id FROM departments WHERE id=@department AND active=1", values, transaction, cancellationToken);
            if (department.Count == 0) throw new ApiFault(400, "Invalid internal department.");
        }
        if ((await Database.QueryAsync(connection, "SELECT id FROM categories WHERE id=@category AND active=1", values, transaction, cancellationToken)).Count == 0) throw new ApiFault(400, "Invalid category.");
        if ((await Database.QueryAsync(connection, "SELECT code FROM ticket_sources WHERE code=@source AND active=1", values, transaction, cancellationToken)).Count == 0) throw new ApiFault(400, "Invalid ticket source.");
        if (input.AssignedUserId.HasValue) await ValidateAssignedUserAsync(connection, transaction, input.AssignedUserId.Value, input.ClientId, input.SiteId, cancellationToken);
        foreach (var tagId in (input.TagIds ?? []).Distinct())
            if ((await Database.QueryAsync(connection, "SELECT id FROM tags WHERE id=@tag AND active=1", new Dictionary<string, object?> { ["tag"] = tagId }, transaction, cancellationToken)).Count == 0) throw new ApiFault(400, "Invalid tag.");

        var ticketId = Guid.NewGuid();
        var number = await TicketNumbering.NextTicketAsync(connection, transaction, input.ClientId, input.SiteId, cancellationToken);
        values["id"] = ticketId; values["number"] = number; values["location"] = string.IsNullOrWhiteSpace(input.LocationText) ? null : input.LocationText.Trim(); values["assigned"] = input.AssignedUserId;
        values["subject"] = input.Subject.Trim(); values["description"] = input.Description.Trim();
        values["reference"] = input.SourceReferenceId; values["conversation"] = input.SourceConversationId; values["message"] = input.SourceMessageId; values["url"] = input.SourceUrl; values["thread"] = input.ExternalThreadId;
        await Database.ExecuteAsync(connection, """
            INSERT INTO tickets(id,ticket_number,client_id,requester_id,site_id,department_id,location_text,category_id,assigned_user_id,created_by_user_id,subject,description,source,priority,source_reference_id,source_conversation_id,source_message_id,source_url,external_thread_id)
            VALUES(@id,@number,@client,@requester,@site,@department,@location,@category,@assigned,@actor,@subject,@description,@source,@priority,@reference,@conversation,@message,@url,@thread)
            """, new Dictionary<string, object?>(values) { ["priority"] = input.Priority }, transaction, cancellationToken);
        foreach (var tagId in (input.TagIds ?? []).Distinct())
            await Database.ExecuteAsync(connection, "INSERT INTO ticket_tags(ticket_id,tag_id,added_by_user_id) VALUES(@ticket,@tag,@actor)", new Dictionary<string, object?> { ["ticket"] = ticketId, ["tag"] = tagId, ["actor"] = actor.Id }, transaction, cancellationToken);
        if (input.AssignedUserId is Guid assigned)
        {
            var assignment = await Database.QueryAsync(connection, "INSERT INTO ticket_assignments(ticket_id,assigned_user_id,actor_user_id,reason) VALUES(@ticket,@assigned,@actor,'Initial assignment'); SELECT LAST_INSERT_ID() AS id", new Dictionary<string, object?> { ["ticket"] = ticketId, ["assigned"] = assigned, ["actor"] = actor.Id }, transaction, cancellationToken);
            await NotificationOutbox.EnqueueAsync(connection, transaction, assigned, ticketId, "TICKET_ASSIGNED", number + " assigned to you", input.Subject.Trim(), "assignment:" + Convert.ToInt64(assignment[0]["id"]).ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
        }
        foreach (var attachment in attachments)
        {
            await Database.ExecuteAsync(connection, "INSERT INTO ticket_attachments(id,ticket_id,uploaded_by_user_id,file_name,content_type,size_bytes,storage_key,sha256,content,visibility) VALUES(@attachment,@ticket,@actor,@name,@type,@size,@storage,@hash,@content,@visibility)", new Dictionary<string, object?>
            {
                ["attachment"] = attachment.Id,
                ["ticket"] = ticketId,
                ["actor"] = actor.Id,
                ["name"] = attachment.Input.FileName.Trim(),
                ["type"] = attachment.Input.ContentType,
                ["size"] = attachment.Content.Length,
                ["storage"] = "database:" + attachment.Id.ToString("N"),
                ["hash"] = attachment.Hash,
                ["content"] = attachment.Content,
                ["visibility"] = attachment.Input.Visibility ?? "INTERNAL"
            }, transaction, cancellationToken);
        }
        await Database.ExecuteAsync(connection, """
            INSERT INTO ticket_sla(ticket_id,policy_id,calendar_id,target_priority,response_warning_at,response_due_at,resolution_warning_at,resolution_due_at)
            SELECT @ticket,p.id,p.calendar_id,p.priority,sla_add_business_minutes(p.calendar_id,UTC_TIMESTAMP(6),CAST(CEILING(p.response_minutes*p.warning_percent/100.0) AS SIGNED)),
              sla_add_business_minutes(p.calendar_id,UTC_TIMESTAMP(6),p.response_minutes),sla_add_business_minutes(p.calendar_id,UTC_TIMESTAMP(6),CAST(CEILING(p.resolution_minutes*p.warning_percent/100.0) AS SIGNED)),
              sla_add_business_minutes(p.calendar_id,UTC_TIMESTAMP(6),p.resolution_minutes) FROM sla_policies p WHERE p.priority=@priority AND p.active=1
            """, new Dictionary<string, object?> { ["ticket"] = ticketId, ["priority"] = input.Priority }, transaction, cancellationToken);
        await HistoryAsync(connection, transaction, ticketId, actor.Id, "TICKET.CREATED", null, new { ticketNumber = number, source = input.Source }, cancellationToken);
        await Audit.WriteAsync(connection, transaction, actor.Id, "TICKET.CREATED", "TICKET", ticketId, new { ticketNumber = number, clientId = input.ClientId }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new { id = ticketId, ticket_number = number };
    }

    public async Task<object> CreateRequesterAsync(Actor actor, CreateRequesterInput input, CancellationToken cancellationToken)
    {
        RequireStaff(actor);
        if (input.ClientId == Guid.Empty || input.Name is null || input.Name.Trim().Length is < 2 or > 200 || input.Email?.Length > 320 || input.Phone?.Length > 50)
            throw new ApiFault(400, "Invalid requester details.");
        var email = input.Email?.Trim(); var phone = input.Phone?.Trim();
        if (string.IsNullOrWhiteSpace(email) && string.IsNullOrWhiteSpace(phone)) throw new ApiFault(400, "Requester email or phone is required.");
        if (!string.IsNullOrWhiteSpace(email) && (!System.Net.Mail.MailAddress.TryCreate(email, out var address) || address.Address != email)) throw new ApiFault(400, "Enter a valid requester email address.");
        if (!actor.Roles.Contains("ADMIN") && !actor.ClientIds.Contains(input.ClientId)) throw new ApiFault(403, "Organization is not assigned.");
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (MySqlTransaction)await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        var client = await Database.QueryAsync(connection, "SELECT id FROM clients WHERE id=@client AND status='ACTIVE' FOR UPDATE", new Dictionary<string, object?> { ["client"] = input.ClientId }, transaction, cancellationToken);
        if (client.Count == 0) throw new ApiFault(400, "Invalid organization.");
        if (!string.IsNullOrWhiteSpace(email))
        {
            var duplicate = await Database.QueryAsync(connection, "SELECT id FROM contacts WHERE client_id=@client AND LOWER(email)=LOWER(@email) AND active=1 LIMIT 1", new Dictionary<string, object?> { ["client"] = input.ClientId, ["email"] = email }, transaction, cancellationToken);
            if (duplicate.Count > 0) throw new ApiFault(409, "A requester with this email already exists.");
        }
        var id = Guid.NewGuid();
        var values = new Dictionary<string, object?> { ["id"] = id, ["client"] = input.ClientId, ["name"] = input.Name.Trim(), ["email"] = string.IsNullOrWhiteSpace(email) ? null : email, ["phone"] = string.IsNullOrWhiteSpace(phone) ? null : phone };
        await Database.ExecuteAsync(connection, "INSERT INTO contacts(id,client_id,name,email,phone,active) VALUES(@id,@client,@name,@email,@phone,1)", values, transaction, cancellationToken);
        await Audit.WriteAsync(connection, transaction, actor.Id, "CONTACT.CREATED", "CONTACT", id, new { clientId = input.ClientId, name = input.Name.Trim() }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new { id, client_id = input.ClientId, name = input.Name.Trim(), email = values["email"] };
    }

    public async Task<object> AddAttachmentAsync(Actor actor, Guid ticketId, TicketAttachmentInput input, CancellationToken cancellationToken)
    {
        RequireStaff(actor);
        if (input.FileName is null || input.FileName.Trim().Length is < 1 or > 255 || input.FileName.Contains('/') || input.FileName.Contains('\\')
            || input.ContentType is null || input.ContentType.Length > 200 || !AttachmentTypes.Contains(input.ContentType)
            || input.Visibility is not (null or "PUBLIC" or "INTERNAL") || string.IsNullOrEmpty(input.ContentBase64) || input.ContentBase64.Length > ((MaxAttachmentBytes + 2) / 3 * 4 + 8))
            throw new ApiFault(400, "Invalid ticket attachment.");
        byte[] content;
        try { content = Convert.FromBase64String(input.ContentBase64); }
        catch (FormatException) { throw new ApiFault(400, "Invalid ticket attachment data."); }
        if (content.Length is < 1 or > MaxAttachmentBytes) throw new ApiFault(400, "Ticket attachments must be no larger than 5 MB.");
        var values = TicketReadService.Parameters(actor); values["ticket"] = ticketId;
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (MySqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var ticket = await Database.QueryAsync(connection, "SELECT t.id FROM tickets t WHERE t.id=@ticket AND " + AttachmentAccess() + "", values, transaction, cancellationToken);
        if (ticket.Count == 0) throw new ApiFault(404, "Ticket not found.");
        var id = Guid.NewGuid(); var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        await Database.ExecuteAsync(connection, "INSERT INTO ticket_attachments(id,ticket_id,uploaded_by_user_id,file_name,content_type,size_bytes,storage_key,sha256,content,visibility) VALUES(@id,@ticket,@actor,@name,@type,@size,@storage,@hash,@content,@visibility)", new Dictionary<string, object?>
        {
            ["id"] = id,
            ["ticket"] = ticketId,
            ["actor"] = actor.Id,
            ["name"] = input.FileName.Trim(),
            ["type"] = input.ContentType,
            ["size"] = content.Length,
            ["storage"] = "database:" + id.ToString("N"),
            ["hash"] = hash,
            ["content"] = content,
            ["visibility"] = input.Visibility ?? "INTERNAL"
        }, transaction, cancellationToken);
        await HistoryAsync(connection, transaction, ticketId, actor.Id, "TICKET.ATTACHMENT_ADDED", null, new { attachmentId = id, fileName = input.FileName.Trim(), sizeBytes = content.Length, sha256 = hash }, cancellationToken);
        await Audit.WriteAsync(connection, transaction, actor.Id, "TICKET.ATTACHMENT_ADDED", "TICKET", ticketId, new { attachmentId = id, fileName = input.FileName.Trim(), sizeBytes = content.Length, sha256 = hash }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new { id };
    }

    public async Task<IResult> DownloadAttachmentAsync(Actor actor, Guid ticketId, Guid attachmentId, CancellationToken cancellationToken)
    {
        RequireStaff(actor);
        var values = TicketReadService.Parameters(actor); values["ticket"] = ticketId; values["attachment"] = attachmentId;
        await using var connection = await database.OpenAsync(cancellationToken);
        var rows = await Database.QueryAsync(connection, "SELECT a.file_name,a.content_type,a.content FROM ticket_attachments a JOIN tickets t ON t.id=a.ticket_id WHERE a.id=@attachment AND a.ticket_id=@ticket AND " + AttachmentAccess(), values, cancellationToken: cancellationToken);
        if (rows.Count == 0) throw new ApiFault(404, "Attachment not found.");
        var row = rows[0];
        return Results.File((byte[])row["content"]!, (string)row["content_type"]!, (string)row["file_name"]!, enableRangeProcessing: false);
    }

    public async Task<object> UpdateAsync(Actor actor, Guid ticketId, UpdateTicketInput input, CancellationToken cancellationToken)
    {
        RequireStaff(actor);
        if (actor.Roles.Contains("INTERN")) throw new ApiFault(403, "Interns cannot change ticket controls.");
        if (input.Status is null && input.Priority is null && input.CategoryId is null && input.TagIds is null) throw new ApiFault(400, "No ticket changes supplied.");
        if (input.Status is not null && !TicketReadService.Statuses.Contains(input.Status) || input.Priority is not null && !TicketReadService.Priorities.Contains(input.Priority) || input.Reason?.Length > 1000 || (input.TagIds?.Length ?? 0) > 30) throw new ApiFault(400, "Invalid ticket changes.");
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (MySqlTransaction)await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        var values = TicketReadService.Parameters(actor); values["ticket"] = ticketId;
        var current = (await Database.QueryAsync(connection, "SELECT t.status,t.priority,t.category_id FROM tickets t WHERE t.id=@ticket AND " + MutationAccess() + " FOR UPDATE", values, transaction, cancellationToken));
        if (current.Count == 0) throw new ApiFault(404, "Ticket not found.");
        var before = current[0];
        if (input.CategoryId.HasValue && (await Database.QueryAsync(connection, "SELECT id FROM categories WHERE id=@category AND active=1", new Dictionary<string, object?> { ["category"] = input.CategoryId }, transaction, cancellationToken)).Count == 0) throw new ApiFault(400, "Invalid category.");
        foreach (var tag in (input.TagIds ?? []).Distinct()) if ((await Database.QueryAsync(connection, "SELECT id FROM tags WHERE id=@tag AND active=1", new Dictionary<string, object?> { ["tag"] = tag }, transaction, cancellationToken)).Count == 0) throw new ApiFault(400, "Invalid tag.");
        var oldStatus = (string)before["status"]!; var oldPriority = (string)before["priority"]!; var nextStatus = input.Status ?? oldStatus;
        var parameters = new Dictionary<string, object?> { ["ticket"] = ticketId, ["status"] = input.Status, ["priority"] = input.Priority, ["category"] = input.CategoryId };
        await Database.ExecuteAsync(connection, """
            UPDATE tickets SET status=COALESCE(@status,status),priority=COALESCE(@priority,priority),category_id=COALESCE(@category,category_id),
             resolved_at=CASE WHEN @status='RESOLVED' THEN COALESCE(resolved_at,UTC_TIMESTAMP(6)) WHEN @status IS NOT NULL AND @status<>'RESOLVED' THEN NULL ELSE resolved_at END,
             closed_at=CASE WHEN @status='CLOSED' THEN COALESCE(closed_at,UTC_TIMESTAMP(6)) WHEN @status IS NOT NULL AND @status<>'CLOSED' THEN NULL ELSE closed_at END,
             updated_at=UTC_TIMESTAMP(6) WHERE id=@ticket
            """, parameters, transaction, cancellationToken);
        var priorityChanged = input.Priority is not null && input.Priority != oldPriority;
        bool Paused(string status) => status is "WAITING_FOR_CLIENT" or "WAITING_FOR_VENDOR" or "ON_HOLD";
        if (priorityChanged) await Database.ExecuteAsync(connection, """
            UPDATE ticket_sla s JOIN tickets t ON t.id=s.ticket_id JOIN sla_policies p ON p.priority=t.priority SET  s.policy_id=p.id, s.calendar_id=p.calendar_id, s.target_priority=p.priority, s.response_warning_at=sla_add_business_minutes(p.calendar_id,t.created_at,CAST(CEILING(p.response_minutes*p.warning_percent/100.0) AS SIGNED)), s.response_due_at=sla_add_business_minutes(p.calendar_id,t.created_at,p.response_minutes), s.resolution_warning_at=sla_add_business_minutes(p.calendar_id,t.created_at,CAST(CEILING(p.resolution_minutes*p.warning_percent/100.0) AS SIGNED)), s.resolution_due_at=sla_add_business_minutes(p.calendar_id,t.created_at,p.resolution_minutes), s.paused_at=NULL, s.response_warning_remaining_minutes=NULL, s.response_remaining_minutes=NULL, s.resolution_warning_remaining_minutes=NULL, s.resolution_remaining_minutes=NULL, s.updated_at=UTC_TIMESTAMP(6) WHERE t.id=@ticket AND p.active=1
            """, new Dictionary<string, object?> { ["ticket"] = ticketId }, transaction, cancellationToken);
        if (Paused(nextStatus) && (!Paused(oldStatus) || priorityChanged)) await Database.ExecuteAsync(connection, """
            UPDATE ticket_sla SET paused_at=UTC_TIMESTAMP(6),
             response_warning_remaining_minutes=CASE WHEN response_completed_at IS NULL THEN sla_business_minutes_between(calendar_id,UTC_TIMESTAMP(6),response_warning_at) END,
             response_remaining_minutes=CASE WHEN response_completed_at IS NULL THEN sla_business_minutes_between(calendar_id,UTC_TIMESTAMP(6),response_due_at) END,
             resolution_warning_remaining_minutes=CASE WHEN resolution_completed_at IS NULL THEN sla_business_minutes_between(calendar_id,UTC_TIMESTAMP(6),resolution_warning_at) END,
             resolution_remaining_minutes=CASE WHEN resolution_completed_at IS NULL THEN sla_business_minutes_between(calendar_id,UTC_TIMESTAMP(6),resolution_due_at) END,updated_at=UTC_TIMESTAMP(6) WHERE ticket_id=@ticket
            """, new Dictionary<string, object?> { ["ticket"] = ticketId }, transaction, cancellationToken);
        if (Paused(oldStatus) && !Paused(nextStatus)) await Database.ExecuteAsync(connection, """
            UPDATE ticket_sla SET response_warning_at=CASE WHEN response_warning_remaining_minutes IS NULL THEN response_warning_at ELSE sla_add_business_minutes(calendar_id,UTC_TIMESTAMP(6),response_warning_remaining_minutes) END,
             response_due_at=CASE WHEN response_remaining_minutes IS NULL THEN response_due_at ELSE sla_add_business_minutes(calendar_id,UTC_TIMESTAMP(6),response_remaining_minutes) END,
             resolution_warning_at=CASE WHEN resolution_warning_remaining_minutes IS NULL THEN resolution_warning_at ELSE sla_add_business_minutes(calendar_id,UTC_TIMESTAMP(6),resolution_warning_remaining_minutes) END,
             resolution_due_at=CASE WHEN resolution_remaining_minutes IS NULL THEN resolution_due_at ELSE sla_add_business_minutes(calendar_id,UTC_TIMESTAMP(6),resolution_remaining_minutes) END,
             paused_at=NULL,response_warning_remaining_minutes=NULL,response_remaining_minutes=NULL,resolution_warning_remaining_minutes=NULL,resolution_remaining_minutes=NULL,updated_at=UTC_TIMESTAMP(6) WHERE ticket_id=@ticket
            """, new Dictionary<string, object?> { ["ticket"] = ticketId }, transaction, cancellationToken);
        if (input.Status is not null) await Database.ExecuteAsync(connection, "UPDATE ticket_sla SET resolution_completed_at=CASE WHEN @status IN('RESOLVED','CLOSED') THEN COALESCE(resolution_completed_at,UTC_TIMESTAMP(6)) WHEN @status<>'CANCELLED' THEN NULL ELSE resolution_completed_at END,updated_at=UTC_TIMESTAMP(6) WHERE ticket_id=@ticket", new Dictionary<string, object?> { ["ticket"] = ticketId, ["status"] = input.Status }, transaction, cancellationToken);
        if (input.Status is not null && input.Status != oldStatus) await HistoryAsync(connection, transaction, ticketId, actor.Id, "TICKET.STATUS_CHANGED", oldStatus, input.Status, cancellationToken);
        if (input.Priority is not null && input.Priority != oldPriority) await HistoryAsync(connection, transaction, ticketId, actor.Id, "TICKET.PRIORITY_CHANGED", oldPriority, input.Priority, cancellationToken);
        if (input.CategoryId is Guid category && before["category_id"] is Guid priorCategory && category != priorCategory) await HistoryAsync(connection, transaction, ticketId, actor.Id, "TICKET.CATEGORY_CHANGED", priorCategory, category, cancellationToken);
        if (input.TagIds is not null)
        {
            var prior = await Database.QueryAsync(connection, "SELECT tag_id FROM ticket_tags WHERE ticket_id=@ticket", new Dictionary<string, object?> { ["ticket"] = ticketId }, transaction, cancellationToken);
            await Database.ExecuteAsync(connection, "DELETE FROM ticket_tags WHERE ticket_id=@ticket", new Dictionary<string, object?> { ["ticket"] = ticketId }, transaction, cancellationToken);
            foreach (var tag in input.TagIds.Distinct()) await Database.ExecuteAsync(connection, "INSERT INTO ticket_tags(ticket_id,tag_id,added_by_user_id) VALUES(@ticket,@tag,@actor)", new Dictionary<string, object?> { ["ticket"] = ticketId, ["tag"] = tag, ["actor"] = actor.Id }, transaction, cancellationToken);
            await HistoryAsync(connection, transaction, ticketId, actor.Id, "TICKET.TAGS_CHANGED", prior.Select(row => row["tag_id"]).ToArray(), input.TagIds.Distinct().ToArray(), cancellationToken);
        }
        await Audit.WriteAsync(connection, transaction, actor.Id, "TICKET.UPDATED", "TICKET", ticketId, new { input.Status, input.Priority, input.CategoryId, input.TagIds, input.Reason }, cancellationToken);
        await transaction.CommitAsync(cancellationToken); return new { id = ticketId };
    }

    public async Task<object> CommentAsync(Actor actor, Guid ticketId, TicketCommentInput input, CancellationToken cancellationToken)
    {
        RequireStaff(actor);
        if (input.Type is not ("PUBLIC" or "INTERNAL") || input.Body is null || input.Body.Trim().Length is < 1 or > 20000) throw new ApiFault(400, "Invalid ticket comment.");
        if (actor.Roles.Contains("INTERN") && input.Type == "PUBLIC") throw new ApiFault(403, "Interns can add internal notes, not public replies.");
        var values = TicketReadService.Parameters(actor); values["ticket"] = ticketId;
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = (MySqlTransaction)await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        var ticket = await Database.QueryAsync(connection, "SELECT t.assigned_user_id,t.ticket_number,t.subject FROM tickets t WHERE t.id=@ticket AND " + MutationAccess() + " FOR UPDATE", values, transaction, cancellationToken);
        if (ticket.Count == 0) throw new ApiFault(404, "Ticket not found.");
        var id = Guid.NewGuid(); await Database.ExecuteAsync(connection, "INSERT INTO ticket_comments(id,ticket_id,author_user_id,comment_type,body) VALUES(@id,@ticket,@actor,@type,@body)", new Dictionary<string, object?> { ["id"] = id, ["ticket"] = ticketId, ["actor"] = actor.Id, ["type"] = input.Type, ["body"] = input.Body.Trim() }, transaction, cancellationToken);
        if (input.Type == "PUBLIC")
        {
            await Database.ExecuteAsync(connection, "UPDATE tickets SET first_response_at=COALESCE(first_response_at,UTC_TIMESTAMP(6)),updated_at=UTC_TIMESTAMP(6) WHERE id=@ticket", new Dictionary<string, object?> { ["ticket"] = ticketId }, transaction, cancellationToken);
            await Database.ExecuteAsync(connection, "UPDATE ticket_sla SET response_completed_at=COALESCE(response_completed_at,UTC_TIMESTAMP(6)),updated_at=UTC_TIMESTAMP(6) WHERE ticket_id=@ticket", new Dictionary<string, object?> { ["ticket"] = ticketId }, transaction, cancellationToken);
        }
        else await Database.ExecuteAsync(connection, "UPDATE tickets SET updated_at=UTC_TIMESTAMP(6) WHERE id=@ticket", new Dictionary<string, object?> { ["ticket"] = ticketId }, transaction, cancellationToken);
        var action = input.Type == "PUBLIC" ? "TICKET.PUBLIC_REPLY_ADDED" : "TICKET.INTERNAL_NOTE_ADDED";
        await HistoryAsync(connection, transaction, ticketId, actor.Id, action, null, new { commentId = id }, cancellationToken);
        await Audit.WriteAsync(connection, transaction, actor.Id, action, "TICKET", ticketId, new { commentId = id }, cancellationToken);
        if (input.Type == "PUBLIC" && ticket[0]["assigned_user_id"] is Guid assigned && assigned != actor.Id) await NotificationOutbox.EnqueueAsync(connection, transaction, assigned, ticketId, "PUBLIC_REPLY", "Public reply added to " + ticket[0]["ticket_number"], (string)ticket[0]["subject"]!, "public-reply:" + id.ToString("N"), cancellationToken);
        await transaction.CommitAsync(cancellationToken); return new { id };
    }


}
