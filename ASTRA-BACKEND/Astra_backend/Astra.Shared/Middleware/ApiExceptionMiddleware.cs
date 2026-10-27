namespace Astra.Shared.Middleware;

public sealed class ApiExceptionMiddleware(RequestDelegate next, ILogger<ApiExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        try { await next(context); }
        catch (Exception exception) when (!context.Response.HasStarted)
        {
            var status = exception switch
            {
                ApiFault fault => fault.Status,
                BadHttpRequestException bad => bad.StatusCode,
                MySqlConnector.MySqlException sql when sql.Number == 1062 => 409,
                MySqlConnector.MySqlException sql when sql.Number is 1451 or 1452 or 3819 or 1048 or 1406 or 1292 => 400,
                _ => 503
            };
            var reference = Guid.NewGuid();
            logger.LogError("Request failed {Reference} {ErrorType}", reference, exception.GetType().Name);
            context.Response.StatusCode = status;
            var message = exception is ApiFault ? exception.Message
                : status == 400 ? "Invalid request or linked records."
                : status == 409 ? "A conflicting record already exists." : "Service unavailable";
            await context.Response.WriteAsJsonAsync(new { error = new { code = "HTTP_" + status, message, requestId = reference } });
        }
    }
}
