using Microsoft.AspNetCore.Mvc;

namespace Astra.Api.Controller;

[Route("admin")]
public sealed class AdministrationController(AuthService auth, AdministrationService administration) : AstraControllerBase(auth)
{
    [HttpGet("overview")]
    public async Task<IActionResult> GetOverview()
    {
        await AdministratorAsync();
        return Ok(await administration.OverviewAsync(Cancellation));
    }

    [HttpGet("{resource}")]
    public async Task<IActionResult> GetRecords(string resource) =>
        Ok(await administration.ListAsync(await CurrentActorAsync(), resource, Request.Query, Cancellation));
}
