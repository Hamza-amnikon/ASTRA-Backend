using Microsoft.AspNetCore.Mvc;

namespace AdministrationService.API.Controllers;

[Route("admin")]
public sealed class AdministrationController(global::Astra.Shared.Services.AuthService auth, global::AdministrationService.API.Services.AdministrationService administration) : AstraControllerBase(auth)
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
