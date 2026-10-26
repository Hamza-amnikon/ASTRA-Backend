using Microsoft.AspNetCore.Mvc;

namespace Astra.Api.Controller;

[Route("notifications")]
public sealed class NotificationController(AuthService auth, NotificationService notifications) : AstraControllerBase(auth)
{
    [HttpGet]
    public async Task<IActionResult> GetNotifications() =>
        Ok(await notifications.ListAsync(await CurrentActorAsync(), Request.Query["limit"].ToString(), Cancellation));

    [HttpGet("preferences")]
    public async Task<IActionResult> GetPreferences() =>
        Ok(await notifications.ReadPreferencesAsync((await CurrentActorAsync()).Id, Cancellation));

    [HttpPatch("preferences")]
    public async Task<IActionResult> SavePreferences([FromBody] NotificationPreferences input) =>
        Ok(await notifications.SavePreferencesAsync((await CurrentActorAsync()).Id, input, Cancellation));

    [HttpPatch("read-all")]
    public async Task<IActionResult> MarkAllRead()
    {
        await notifications.MarkAsync((await CurrentActorAsync()).Id, null, Cancellation);
        return Ok(new { read = true });
    }

    [HttpPatch("{id:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid id)
    {
        await notifications.MarkAsync((await CurrentActorAsync()).Id, id, Cancellation);
        return Ok(new { id });
    }
}
