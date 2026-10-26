namespace Astra.Api.Middleware;

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
                Microsoft.Data.SqlClient.SqlException sql when sql.Number is 2601 or 2627 => 409,
                Microsoft.Data.SqlClient.SqlException sql when sql.Number == 547 => 400,
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
