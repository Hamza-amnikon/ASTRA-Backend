using System.Text.Json;
using MySqlConnector;

public sealed partial class MySqlStore
{
    private static async Task QueueTicketEvent(MySqlConnection connection, MySqlTransaction tx, string? actor, string ticket, string? comment, string eventName, CancellationToken ct)
    {
        var settings = (await FRows(connection, tx, "SELECT enabled,ticket_address FROM mail_settings WHERE id=1", ct)).FirstOrDefault();
        if (settings is null || !FBool(settings["enabled"]) || settings["ticket_address"] is not string sender)
            return;
        var row = (await FRows(connection, tx, "SELECT ticket_number,subject FROM tickets WHERE id=@id", ct, ("id", ticket))).FirstOrDefault();
        if (row is null)
            return;
        var recipients = (await FRows(connection, tx, "SELECT DISTINCT LOWER(u.email) AS email FROM tickets t JOIN users u ON u.active=1 AND (u.access_expires_at IS NULL OR u.access_expires_at>UTC_TIMESTAMP(6)) WHERE t.id=@id AND (EXISTS(SELECT 1 FROM user_roles r WHERE r.user_id=u.id AND r.role_code='ADMIN') OR ((u.id=t.assigned_user_id AND EXISTS(SELECT 1 FROM user_roles r WHERE r.user_id=u.id AND r.role_code IN ('TECHNICIAN','TEAM_LEAD')) OR EXISTS(SELECT 1 FROM user_roles r WHERE r.user_id=u.id AND r.role_code='TEAM_LEAD')) AND EXISTS(SELECT 1 FROM client_users cu WHERE cu.user_id=u.id AND cu.client_id=t.client_id) AND (NOT EXISTS(SELECT 1 FROM user_sites us WHERE us.user_id=u.id) OR EXISTS(SELECT 1 FROM user_sites us WHERE us.user_id=u.id AND us.site_id=t.site_id))))", ct, ("id", ticket))).Select(x => (string)x["email"]!).Where(x => x != sender && !x.EndsWith(".invalid", StringComparison.OrdinalIgnoreCase) && System.Net.Mail.MailAddress.TryCreate(x, out _)).ToArray();
        if (comment is not null)
        {
            var reply = (await FRows(connection, tx, "SELECT r.address,LOWER(c.email) AS requester,t.ticket_number,t.subject,tc.body FROM tickets t JOIN contacts c ON c.id=t.requester_id JOIN ticket_comments tc ON tc.ticket_id=t.id AND tc.id=@comment AND tc.comment_type='PUBLIC' JOIN client_mail_routes r ON r.client_id=t.client_id AND r.address=t.email_reply_address WHERE t.id=@id AND t.source='EMAIL' AND c.active=1 AND c.email IS NOT NULL AND r.active=1 AND r.outbound_authorized=1", ct, ("id", ticket), ("comment", comment))).SingleOrDefault();
            if (reply is not null)
            {
                var mail = await QueueMail(connection, tx, actor, "TICKET", ValidEmail((string?)reply["address"], "reply sender"), [ValidEmail((string?)reply["requester"], "requester")], $"[{reply["ticket_number"]}] {reply["subject"]}", (string)reply["body"]!, ticket, null, ct);
                await FExecute(connection, tx, "UPDATE mail_messages SET comment_id=@comment WHERE id=@id", ct, ("comment", comment), ("id", mail));
                return;
            }
        }
        if (recipients.Length > 0)
        {
            var id = await QueueMail(connection, tx, actor, "TICKET", sender, recipients, $"[{row["ticket_number"]}] {row["subject"]}", $"{eventName}\n\nTicket: {row["ticket_number"]}\nSubject: {row["subject"]}\nOpen the ticket in your assigned workspace.", ticket, null, ct);
            if (comment is not null)
                await FExecute(connection, tx, "UPDATE mail_messages SET comment_id=@comment WHERE id=@id", ct, ("comment", comment), ("id", id));
        }
    }
    public async Task<object> MailIntake(string actor, string id, JsonElement body, CancellationToken ct)
    {
        await RequireAdmin(actor, ct);
        var client = FGuid(JString(body, "clientId"), "client");
        var requester = FGuid(JString(body, "requesterId"), "requester");
        var category = FGuid(JString(body, "categoryId"), "category");
        var site = JOptional(body, "siteId");
        if (site is not null)
            site = FGuid(site, "site");
        await using var c = CreateConnection();
        await c.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var mail = (await FRows(c, tx, "SELECT * FROM mail_messages WHERE id=@id AND direction='INBOUND' AND channel='TICKET' FOR UPDATE", ct, ("id", id))).FirstOrDefault() ?? throw new KeyNotFoundException();
        if (mail["ticket_id"] is string existing)
        {
            await tx.CommitAsync(ct);
            return (await FQuery("SELECT id,ticket_number FROM tickets WHERE id=@id", ct, ("id", existing)))[0];
        }
        if ((string?)mail["state"] != "REVIEW")
            throw new ArgumentException("This email is no longer awaiting ticket intake.");
        if ((await FRows(c, tx, "SELECT id FROM clients WHERE id=@id AND status='ACTIVE' FOR SHARE", ct, ("id", client))).Count == 0)
            throw new ArgumentException("Choose an active client.");
        var contact = (await FRows(c, tx, "SELECT id,site_id FROM contacts WHERE id=@id AND client_id=@client AND active=1 AND LOWER(email)=LOWER(@email) FOR SHARE", ct, ("id", requester), ("client", client), ("email", mail["sender_address"]))).FirstOrDefault() ?? throw new ArgumentException("Choose an active contact matching the sender and client.");
        if (site is null)
            site = (string?)contact["site_id"];
        if (contact["site_id"] is string fixedSite && site != fixedSite)
            throw new ArgumentException("The selected site does not match this contact.");
        if (site is not null && (await FRows(c, tx, "SELECT id FROM sites WHERE id=@id AND client_id=@client AND active=1 FOR SHARE", ct, ("id", site), ("client", client))).Count == 0)
            throw new ArgumentException("Choose a site belonging to this client.");
        if ((await FRows(c, tx, "SELECT id FROM categories WHERE id=@id AND active=1 FOR SHARE", ct, ("id", category))).Count == 0)
            throw new ArgumentException("Choose an active category.");
        if ((await FRows(c, tx, "SELECT code FROM ticket_sources WHERE code='EMAIL' AND active=1 FOR SHARE", ct)).Count == 0)
            throw new ArgumentException("Enable the email ticket source first.");
        var ticket = Guid.NewGuid().ToString("D");
        var number = await AllocateTicketNumber(c, tx, client, site, ct);
        var subject = ((string?)mail["subject"] ?? "").Trim();
        if (subject.Length == 0)
            subject = "Email support request";
        if (subject.Length > 200)
            subject = subject[..200];
        var description = ((string?)mail["body"] ?? "").Trim();
        if (description.Length == 0)
            description = "(No message text)";
        await FExecute(c, tx, "INSERT INTO tickets(id,ticket_number,client_id,requester_id,site_id,category_id,subject,description,source,priority,created_by_user_id,source_message_id,email_reply_address) VALUES(@id,@number,@client,@requester,@site,@category,@subject,@description,'EMAIL','P3_MEDIUM',@actor,@message,@reply)", ct, ("id", ticket), ("number", number), ("client", client), ("requester", requester), ("site", site), ("category", category), ("subject", subject), ("description", description), ("actor", actor), ("message", mail["message_id"]), ("reply", mail["mailbox"]));
        await SetInitialTicketSla(c, tx, ticket, "P3_MEDIUM", ct);
        await FExecute(c, tx, "UPDATE mail_messages SET ticket_id=@ticket,state='RECEIVED',last_error_code='MANUAL_INTAKE',processed_at=UTC_TIMESTAMP(6) WHERE id=@id", ct, ("ticket", ticket), ("id", id));
        await FExecute(c, tx, "INSERT INTO ticket_history(ticket_id,actor_user_id,action,new_value) VALUES(@ticket,@actor,'TICKET.CREATED',JSON_OBJECT('source','EMAIL'))", ct, ("ticket", ticket), ("actor", actor));
        await AddAudit(c, tx, actor, "EMAIL.TICKET_CREATED", "EMAIL", id, new
        {
            ticketId = ticket
        }, ct);
        await QueueTicketEvent(c, tx, actor, ticket, null, "Email ticket created", ct);
        await tx.CommitAsync(ct);
        return new
        {
            id = ticket,
            ticket_number = number
        };
    }
}
