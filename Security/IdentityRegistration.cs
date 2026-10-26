using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Astra.Api.Security;

public static class IdentityRegistration
{
    public static void AddAstraIdentity(this WebApplicationBuilder builder)
    {
        var identity = IdentityOptions.Read(builder.Configuration, builder.Environment);
        builder.Services.AddSingleton(identity);
        builder.Services.AddSingleton<ISessionStore, RedisSessionStore>();
        builder.Services.AddScoped<AuthService>();
        var protection = builder.Services.AddDataProtection().SetApplicationName("ASTRA native backend");
        var keyDirectory = builder.Configuration["DATA_PROTECTION_KEY_DIRECTORY"];
        if (!string.IsNullOrWhiteSpace(keyDirectory)) protection.PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));
        else if (identity.Configured && builder.Environment.IsProduction()) throw new InvalidOperationException("Persistent protected OIDC keys must be configured.");
        var authentication = builder.Services.AddAuthentication();
        if (!identity.Configured) return;
        authentication.AddCookie("oidc-terminal", options =>
        {
            // Application sessions use Redis; an OIDC ticket must never issue a
            // separate authorization cookie that bypasses session revocation.
            options.Events.OnSigningIn = _ => throw new InvalidOperationException("An OIDC ticket was not handled.");
        }).AddOpenIdConnect("entra", options =>
        {
            options.SignInScheme = "oidc-terminal";
            options.Authority = $"https://login.microsoftonline.com/{identity.TenantId}/v2.0";
            options.ClientId = identity.ClientId;
            options.ClientSecret = identity.ClientSecret;
            options.CallbackPath = "/auth/callback";
            options.RemoteSignOutPath = PathString.Empty;
            options.SignedOutCallbackPath = PathString.Empty;
            options.ResponseType = OpenIdConnectResponseType.Code;
            options.ResponseMode = OpenIdConnectResponseMode.Query;
            options.UsePkce = true;
            options.MapInboundClaims = false;
            options.SaveTokens = false;
            options.GetClaimsFromUserInfoEndpoint = false;
            options.Scope.Clear();
            options.Scope.Add("openid");
            options.Scope.Add("profile");
            options.Scope.Add("email");
            options.RemoteAuthenticationTimeout = TimeSpan.FromMinutes(10);
            options.BackchannelTimeout = TimeSpan.FromSeconds(10);
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateAudience = true,
                ValidAudience = identity.ClientId,
                ValidateIssuer = true,
                ValidIssuer = $"https://login.microsoftonline.com/{identity.TenantId}/v2.0",
                ValidateLifetime = true,
                RequireExpirationTime = true,
                RequireSignedTokens = true,
                ValidateIssuerSigningKey = true,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                ClockSkew = TimeSpan.FromSeconds(60),
                NameClaimType = "name"
            };
            // The existing React proxy sends a GET callback at /api/auth/callback.
            // Both short-lived browser cookies must survive the proxy individually.
            foreach (var cookie in new[] { options.CorrelationCookie, options.NonceCookie })
            {
                cookie.Path = "/api/auth/callback";
                cookie.SameSite = SameSiteMode.Lax;
                cookie.HttpOnly = true;
                cookie.SecurePolicy = identity.BaseUri.Scheme == "https" ? CookieSecurePolicy.Always : CookieSecurePolicy.None;
            }
            options.Events = new OpenIdConnectEvents
            {
                OnRedirectToIdentityProvider = context =>
                {
                    context.ProtocolMessage.RedirectUri = new Uri(identity.BaseUri, "/api/auth/callback").AbsoluteUri;
                    return Task.CompletedTask;
                },
                OnTicketReceived = async context =>
                {
                    var service = context.HttpContext.RequestServices.GetRequiredService<AuthService>();
                    var login = await service.LoginAsync(context.Principal!, context.ReturnUri, context.HttpContext.RequestAborted);
                    context.Response.Cookies.Append(AuthService.CookieName, login.Token, service.CookieOptions());
                    context.Response.StatusCode = StatusCodes.Status303SeeOther;
                    context.Response.Headers.Location = login.Destination;
                    context.HandleResponse();
                },
                OnRemoteFailure = async context =>
                {
                    // Provider diagnostics may contain tokens and user identifiers.
                    context.HandleResponse();
                    context.Response.StatusCode = 401;
                    await context.Response.WriteAsJsonAsync(new { error = new { code = "HTTP_401", message = "Sign-in could not be completed." } });
                }
            };
        });
    }


}
