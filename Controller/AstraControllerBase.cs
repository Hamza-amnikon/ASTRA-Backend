using Microsoft.AspNetCore.Mvc;

namespace Astra.Api.Controller;

[ApiController]
public abstract class AstraControllerBase(AuthService auth) : ControllerBase
{
    protected CancellationToken Cancellation => HttpContext.RequestAborted;
    protected Task<Actor> CurrentActorAsync() => auth.CurrentAsync(HttpContext);
    protected Task<Actor> RequirePermissionAsync(string permission) => auth.RequirePermissionAsync(HttpContext, permission);

    protected async Task<Actor> AdministratorAsync()
    {
        var actor = await CurrentActorAsync();
        CoreDataService.RequireAdministrator(actor);
        return actor;
    }
}
