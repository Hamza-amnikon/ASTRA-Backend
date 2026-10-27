using Microsoft.AspNetCore.Mvc;

namespace GroupService.API.Controllers;

[Route("admin/groups")]
public sealed class GroupController(global::Astra.Shared.Services.AuthService auth, global::GroupService.API.Services.GroupService groups) : AstraControllerBase(auth)
{
    [HttpGet("options")]
    public async Task<IActionResult> GetOptions()
    {
        await AdministratorAsync();
        return Ok(await groups.OptionsAsync(Cancellation));
    }

    [HttpGet]
    public async Task<IActionResult> GetGroups()
    {
        await AdministratorAsync();
        return Ok(await groups.ListAsync(Request.Query, Cancellation));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetGroup(Guid id)
    {
        await AdministratorAsync();
        return Ok(await groups.DetailAsync(id, Cancellation));
    }

    [HttpPost]
    public async Task<IActionResult> CreateGroup([FromBody] GroupInput input) =>
        StatusCode(StatusCodes.Status201Created, await groups.SaveAsync(await AdministratorAsync(), input, null, Cancellation));

    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> UpdateGroup(Guid id, [FromBody] GroupInput input) =>
        Ok(await groups.SaveAsync(await AdministratorAsync(), input, id, Cancellation));
}
