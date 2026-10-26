using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;

namespace Astra.Api.Controller;

public sealed class CoreDataController(AuthService auth, CoreDataService core) : AstraControllerBase(auth)
{
    [HttpGet("/roles")]
    [HttpGet("/permissions")]
    [HttpGet("/ticket-sources")]
    [HttpGet("/users")]
    [HttpGet("/clients")]
    [HttpGet("/contacts")]
    [HttpGet("/sites")]
    [HttpGet("/assets")]
    [HttpGet("/categories")]
    [HttpGet("/tags")]
    [HttpGet("/audit-logs")]
    public async Task<IActionResult> GetRecords()
    {
        var resource = Request.Path.Value!.Trim('/');
        Guid? client = null;
        if (resource is "contacts" or "sites" or "assets")
        {
            RequestQuery.Only(Request.Query, "clientId");
            client = RequestQuery.Id(Request.Query, "clientId");
        }
        return Ok(await core.ListAsync(await CurrentActorAsync(), resource, client, Cancellation));
    }

    [HttpPost("/clients")]
    [HttpPost("/contacts")]
    [HttpPost("/sites")]
    [HttpPost("/assets")]
    [HttpPost("/categories")]
    [HttpPost("/tags")]
    public async Task<IActionResult> CreateRecord([FromBody] JsonObject input) =>
        StatusCode(StatusCodes.Status201Created,
            await core.CreateAsync(await CurrentActorAsync(), Request.Path.Value!.Trim('/'), input, Cancellation));

    [HttpPost("/users/resolve")]
    public async Task<IActionResult> ResolveUser([FromBody] JsonObject input) =>
        Ok(new { items = await core.ResolveEntraUserAsync(await CurrentActorAsync(), input, Cancellation) });

    [HttpPost("/users")]
    public async Task<IActionResult> CreateUser([FromBody] ProvisionUserInput input) =>
        StatusCode(StatusCodes.Status201Created, await core.CreateUserAsync(await CurrentActorAsync(), input, Cancellation));

    [HttpPost("/ticket-sources")]
    [HttpPost("/sources")]
    public async Task<IActionResult> CreateSource([FromBody] JsonObject input) =>
        Ok(await core.CreateTicketSourceAsync(await CurrentActorAsync(), input, Cancellation));

    [HttpPatch("/ticket-sources/{code}")]
    public async Task<IActionResult> UpdateSource(string code, [FromBody] JsonObject input) =>
        Ok(await core.UpdateTicketSourceAsync(await CurrentActorAsync(), code, input, Cancellation));
}
