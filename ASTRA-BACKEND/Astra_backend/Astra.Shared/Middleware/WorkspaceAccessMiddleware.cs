namespace Astra.Shared.Middleware;

public sealed class WorkspaceAccessMiddleware(RequestDelegate next, IWebHostEnvironment environment)
{
    public async Task InvokeAsync(HttpContext context, AuthService auth)
    {
        var path = context.Request.Path.Value ?? "/";
        var publicRoute = path is "/health" or "/healthz" or "/ready"
            or "/auth/status" or "/auth/login" or "/auth/local-login" or "/auth/callback";
        var developmentDocumentation = environment.IsDevelopment()
            && (context.Request.Path.StartsWithSegments("/swagger") || context.Request.Path.StartsWithSegments("/openapi"));
        if (!publicRoute && !developmentDocumentation)
        {
            var actor = await auth.CurrentAsync(context);
            WorkspacePolicy.RequireOperational(actor, context.Request.Method, path);
            if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)
                && !HttpMethods.IsOptions(context.Request.Method))
                await auth.VerifyCsrfAsync(context);
        }
        await next(context);
    }
}
