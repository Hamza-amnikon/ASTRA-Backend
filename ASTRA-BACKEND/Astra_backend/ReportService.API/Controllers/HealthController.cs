using Microsoft.AspNetCore.Mvc;

namespace ReportService.API.Controllers;

[ApiController]
public sealed class HealthController(Database database, ISessionStore sessions) : ControllerBase
{
    [HttpGet("/health")]
    public IActionResult Health() => Ok(new { status = "ok" });

    [HttpGet("/healthz")]
    public IActionResult Liveness() => Ok(new { status = "ok", runtime = "aspnetcore", release = "migration" });

    [HttpGet("/ready")]
    public async Task<IActionResult> Ready()
    {
        await using var connection = await database.OpenAsync(HttpContext.RequestAborted);
        var result = await Database.QueryAsync(connection,
            "SELECT version FROM app_schema_state WHERE id=1 AND version>=24", cancellationToken: HttpContext.RequestAborted);
        await sessions.CheckAsync();
        return result.Count == 1 ? Ok(new { status = "ready" })
            : StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = new { code = "HTTP_503", message = "Service unavailable" } });
    }
}
