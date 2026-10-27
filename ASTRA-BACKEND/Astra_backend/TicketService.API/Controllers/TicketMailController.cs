using Microsoft.AspNetCore.Mvc;

namespace TicketService.API.Controllers;

public sealed class TicketMailController(global::Astra.Shared.Services.AuthService auth, TicketMailService mail)
    : AstraControllerBase(auth)
{
    [HttpGet("/mail/tickets/{ticketId:guid}")]
    public async Task<IActionResult> GetMessages(Guid ticketId) =>
        Ok(await mail.ListAsync(await CurrentActorAsync(), ticketId, Cancellation));

    [HttpGet("/mail/attachments/{attachmentId:guid}")]
    public async Task<IResult> Download(Guid attachmentId) =>
        await mail.DownloadAsync(await CurrentActorAsync(), attachmentId, Cancellation);
}
