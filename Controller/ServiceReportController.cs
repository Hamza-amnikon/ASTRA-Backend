using Microsoft.AspNetCore.Mvc;

namespace Astra.Api.Controller;

[Route("service-reports")]
public sealed class ServiceReportController(AuthService auth, ServiceReportService reports) : AstraControllerBase(auth)
{
    [HttpGet]
    public async Task<IActionResult> GetReport()
    {
        if (Request.Query.Keys.Any(key => key is not ("from" or "to" or "clientId")))
            throw new ApiFault(400, "Invalid report filters.");
        Guid? client = null;
        if (Request.Query.TryGetValue("clientId", out var value))
        {
            if (!Guid.TryParse(value.ToString(), out var id)) throw new ApiFault(400, "Invalid organization.");
            client = id;
        }
        return Ok(await reports.ReportAsync(await CurrentActorAsync(), Request.Query["from"].ToString(),
            Request.Query["to"].ToString(), client, Cancellation));
    }
}
