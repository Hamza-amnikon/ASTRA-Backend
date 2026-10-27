using Astra.Shared.Data;
using Astra.Shared.Security;

namespace TicketService.API.Services;

public sealed class TicketMailService(Database database)
{
    public async Task<object> ListAsync(Actor actor, Guid ticketId, CancellationToken cancellationToken)
    {
        TicketReadService.RequireStaff(actor);
        var parameters = TicketReadService.Parameters(actor);
        parameters["ticket"] = ticketId;
        await using var connection = await database.OpenAsync(cancellationToken);
        if ((await Database.QueryAsync(connection,
            "SELECT t.id FROM tickets t WHERE t.id=@ticket AND " + TicketReadService.Access(),
            parameters, cancellationToken: cancellationToken)).Count == 0)
            throw new ApiFault(404, "Ticket not found.");
        var messages = await Database.QueryAsync(connection, """
            SELECT id,direction,channel,sender_address,recipient_addresses,subject,body,state,created_at
            FROM mail_messages WHERE ticket_id=@ticket ORDER BY created_at,id
            """, parameters, cancellationToken: cancellationToken);
        var attachments = await Database.QueryAsync(connection, """
            SELECT a.id,a.message_id,a.file_name,a.content_type,a.size_bytes
            FROM mail_attachments a JOIN mail_messages m ON m.id=a.message_id
            WHERE m.ticket_id=@ticket ORDER BY a.created_at,a.id
            """, parameters, cancellationToken: cancellationToken);
        foreach (var message in messages)
            message["attachments"] = attachments.Where(attachment =>
                (Guid)attachment["message_id"]! == (Guid)message["id"]!).ToArray();
        return messages;
    }

    public async Task<IResult> DownloadAsync(Actor actor, Guid attachmentId, CancellationToken cancellationToken)
    {
        TicketReadService.RequireStaff(actor);
        var parameters = TicketReadService.Parameters(actor);
        parameters["attachment"] = attachmentId;
        await using var connection = await database.OpenAsync(cancellationToken);
        var rows = await Database.QueryAsync(connection,
            "SELECT a.file_name,a.content_type,a.content FROM mail_attachments a " +
            "JOIN mail_messages m ON m.id=a.message_id JOIN tickets t ON t.id=m.ticket_id " +
            "WHERE a.id=@attachment AND " + TicketReadService.Access(), parameters, cancellationToken: cancellationToken);
        if (rows.Count == 0) throw new ApiFault(404, "Email attachment not found.");
        return Results.File((byte[])rows[0]["content"]!, (string)rows[0]["content_type"]!,
            (string)rows[0]["file_name"]!, enableRangeProcessing: false);
    }
}
