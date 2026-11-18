using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

internal static class AuthSessionPolicy
{
    public static TimeSpan Lifetime(IConfiguration configuration) =>
        TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("SESSION_TTL_SECONDS", 28800), 900, 86400));

    public static void Configure(CookieAuthenticationOptions options, TimeSpan lifetime)
    {
        options.ExpireTimeSpan = lifetime;
        options.SlidingExpiration = true;
    }

    public static AuthenticationProperties SignIn(TimeSpan lifetime, DateTimeOffset? now = null) => new()
    {
        IsPersistent = false,
        AllowRefresh = true,
        ExpiresUtc = (now ?? DateTimeOffset.UtcNow).Add(lifetime)
    };
}
