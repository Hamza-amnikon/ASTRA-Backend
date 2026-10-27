using Microsoft.AspNetCore.Mvc;

namespace SlaService.API.Controllers;

[Route("sla")]
public sealed class SlaController(global::Astra.Shared.Services.AuthService auth, global::SlaService.API.Services.SlaConfigurationService sla) : AstraControllerBase(auth)
{
    [HttpGet("policies")]
    public async Task<IActionResult> GetPolicies()
    {
        await RequirePermissionAsync("SLA_MANAGE");
        return Ok(await sla.PoliciesAsync(Cancellation));
    }

    [HttpGet("calendars")]
    public async Task<IActionResult> GetCalendars()
    {
        await RequirePermissionAsync("SLA_MANAGE");
        return Ok(await sla.CalendarsAsync(Cancellation));
    }

    [HttpPost("calendars")]
    public async Task<IActionResult> CreateCalendar([FromBody] CalendarInput input) =>
        StatusCode(StatusCodes.Status201Created,
            await sla.SaveCalendarAsync(await RequirePermissionAsync("SLA_MANAGE"), input, null, Cancellation));

    [HttpPatch("calendars/{id:guid}")]
    public async Task<IActionResult> UpdateCalendar(Guid id, [FromBody] CalendarInput input) =>
        Ok(await sla.SaveCalendarAsync(await RequirePermissionAsync("SLA_MANAGE"), input, id, Cancellation));

    [HttpPatch("policies/{priority}")]
    public async Task<IActionResult> UpdatePolicy(string priority, [FromBody] SlaPolicyInput input) =>
        Ok(await sla.SavePolicyAsync(await RequirePermissionAsync("SLA_MANAGE"), priority, input, Cancellation));
}
