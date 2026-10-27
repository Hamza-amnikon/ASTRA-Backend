using Microsoft.AspNetCore.Mvc;

namespace TicketService.API.Controllers;

[Route("tickets")]
public sealed class TicketController(global::Astra.Shared.Services.AuthService auth, global::Astra.Shared.Services.TicketReadService reader, global::TicketService.API.Services.TicketWriteService writer, SavedTicketViewService savedViews)
    : AstraControllerBase(auth)
{
    [HttpGet]
    public async Task<IActionResult> GetTickets([FromQuery] TicketQuery query) => Ok(await reader.ListAsync(
        await CurrentActorAsync(), query.Q, query.Scope, query.Status, query.Priority,
        query.ClientId, query.SiteId, query.AssignedUserId, query.CategoryId, query.TagId,
        query.Source, query.SlaStatus, query.DashboardFilter, query.From, query.To,
        query.Page ?? 1, query.PageSize ?? 25, Cancellation));

    [HttpGet("options")]
    public async Task<IActionResult> GetOptions() => Ok(await reader.OptionsAsync(await CurrentActorAsync(), Cancellation));

    [HttpGet("views")]
    public async Task<IActionResult> GetSavedViews() =>
        Ok(await savedViews.ListAsync(await CurrentActorAsync(), Cancellation));

    [HttpPost("views")]
    public async Task<IActionResult> SaveView([FromBody] SaveTicketViewInput input) =>
        Ok(await savedViews.SaveAsync(await CurrentActorAsync(), input, Cancellation));

    [HttpDelete("views/{id:guid}")]
    public async Task<IActionResult> DeleteView(Guid id) =>
        Ok(await savedViews.DeleteAsync(await CurrentActorAsync(), id, Cancellation));

    [HttpGet("dashboard")]
    public async Task<IActionResult> GetDashboard() => Ok(await reader.DashboardAsync(
        await CurrentActorAsync(), Request.Query["scope"].ToString() == "all", Cancellation));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetTicket(Guid id) => Ok(await reader.DetailAsync(await CurrentActorAsync(), id, Cancellation));

    [HttpPost]
    public async Task<IActionResult> CreateTicket([FromBody] CreateTicketInput input) =>
        StatusCode(StatusCodes.Status201Created, await writer.CreateAsync(await CurrentActorAsync(), input, Cancellation));

    [HttpPost("requesters")]
    public async Task<IActionResult> CreateRequester([FromBody] CreateRequesterInput input) =>
        StatusCode(StatusCodes.Status201Created, await writer.CreateRequesterAsync(await CurrentActorAsync(), input, Cancellation));

    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> UpdateTicket(Guid id, [FromBody] UpdateTicketInput input) =>
        Ok(await writer.UpdateAsync(await CurrentActorAsync(), id, input, Cancellation));

    [HttpPost("{id:guid}/comments")]
    public async Task<IActionResult> AddComment(Guid id, [FromBody] TicketCommentInput input) =>
        Ok(await writer.CommentAsync(await CurrentActorAsync(), id, input, Cancellation));

    [HttpPost("{id:guid}/attachments")]
    public async Task<IActionResult> AddAttachment(Guid id, [FromBody] TicketAttachmentInput input) =>
        Ok(await writer.AddAttachmentAsync(await CurrentActorAsync(), id, input, Cancellation));

    [HttpGet("{id:guid}/attachments/{attachmentId:guid}")]
    public async Task<IResult> DownloadAttachment(Guid id, Guid attachmentId) =>
        await writer.DownloadAttachmentAsync(await CurrentActorAsync(), id, attachmentId, Cancellation);
}
