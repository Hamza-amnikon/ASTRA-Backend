using Microsoft.AspNetCore.Mvc;

namespace CoreDataService.API.Controllers;

public sealed class InternalDirectoryController(global::Astra.Shared.Services.AuthService auth, CoreDataService.API.Services.CoreDataService core)
    : AstraControllerBase(auth)
{
    [HttpGet("/internal/directory")]
    public async Task<IActionResult> GetDirectory() =>
        Ok(await core.StaffDirectoryAsync(await CurrentActorAsync(), Cancellation));

    [HttpGet("/internal/departments")]
    public async Task<IActionResult> GetDepartments() =>
        Ok(await core.DepartmentsAsync(await CurrentActorAsync(), Cancellation));
}
