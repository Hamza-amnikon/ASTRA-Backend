using Microsoft.AspNetCore.Mvc;

namespace ClientDomainService.API.Controllers;

[Route("admin/client-domains")]
public sealed class ClientDomainController(global::Astra.Shared.Services.AuthService auth, global::ClientDomainService.API.Services.ClientDomainService domains) : AstraControllerBase(auth)
{
    [HttpGet]
    public async Task<IActionResult> GetDomains()
    {
        await AdministratorAsync();
        RequestQuery.Only(Request.Query, "siteId");
        return Ok(await domains.ListAsync(RequestQuery.Id(Request.Query, "siteId"), Cancellation));
    }

    [HttpPost]
    public async Task<IActionResult> CreateDomain([FromBody] DomainInput input) =>
        StatusCode(StatusCodes.Status201Created, await domains.SaveAsync(await AdministratorAsync(), input, null, Cancellation));

    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> UpdateDomain(Guid id, [FromBody] DomainInput input) =>
        Ok(await domains.SaveAsync(await AdministratorAsync(), input, id, Cancellation));
}
