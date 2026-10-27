using Microsoft.AspNetCore.Mvc;

namespace ProblemService.API.Controllers;

[Route("problems")]
public sealed class ProblemController(global::Astra.Shared.Services.AuthService auth, global::ProblemService.API.Services.ProblemService problems) : AstraControllerBase(auth)
{
    [HttpGet("options")]
    public async Task<IActionResult> GetOptions() => Ok(await problems.OptionsAsync(await CurrentActorAsync(), Cancellation));

    [HttpGet]
    public async Task<IActionResult> GetProblems([FromQuery] string? q, [FromQuery] string? status,
        [FromQuery] Guid? clientId, [FromQuery] int page = 1, [FromQuery] int pageSize = 20) =>
        Ok(await problems.ListAsync(await CurrentActorAsync(), q, status, clientId, page, pageSize, Cancellation));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetProblem(Guid id) => Ok(await problems.DetailAsync(await CurrentActorAsync(), id, Cancellation));

    [HttpPost]
    public async Task<IActionResult> CreateProblem([FromBody] CreateProblemInput input) =>
        Ok(await problems.CreateAsync(await CurrentActorAsync(), input, Cancellation));

    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> UpdateProblem(Guid id, [FromBody] UpdateProblemInput input) =>
        Ok(await problems.UpdateAsync(await CurrentActorAsync(), id, input, Cancellation));

    [HttpPost("{id:guid}/tickets")]
    public async Task<IActionResult> LinkTicket(Guid id, [FromBody] LinkProblemTicketInput input) =>
        Ok(await problems.LinkAsync(await CurrentActorAsync(), id, input.TicketId, false, Cancellation));

    [HttpDelete("{id:guid}/tickets/{ticketId:guid}")]
    public async Task<IActionResult> UnlinkTicket(Guid id, Guid ticketId) =>
        Ok(await problems.LinkAsync(await CurrentActorAsync(), id, ticketId, true, Cancellation));

    [HttpPost("{id:guid}/notes")]
    public async Task<IActionResult> AddNote(Guid id, [FromBody] ProblemNoteInput input) =>
        Ok(await problems.NoteAsync(await CurrentActorAsync(), id, input.Body, Cancellation));
}
