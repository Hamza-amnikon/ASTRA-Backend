using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;

namespace Astra.Api.Controller;

[ApiController]
[Route("auth")]
public sealed class AuthController(AuthService auth, IdentityOptions options) : ControllerBase
{
    [HttpGet("status")]
    public IActionResult GetStatus() => Ok(new
    {
        configured = options.Configured || auth.LocalAuthEnabled,
        microsoftConfigured = options.Configured,
        localAuth = auth.LocalAuthEnabled
    });

    public sealed record LocalLoginRequest(string? Username, string? Password);

    [HttpPost("local-login")]
    public async Task<IActionResult> LocalLogin([FromBody] LocalLoginRequest request)
    {
        var login = await auth.LocalLoginAsync(request.Username, request.Password);
        Response.Cookies.Append(AuthService.CookieName, login.Token, auth.CookieOptions());
        return Ok(new { loggedIn = true });
    }

    [HttpGet("login")]
    public async Task<IActionResult> Login()
    {
        if (!options.Configured) throw new ApiFault(503, "Identity provider not configured.");
        await HttpContext.ChallengeAsync("entra", new AuthenticationProperties
        {
            RedirectUri = IdentityOptions.SafeReturnTo(Request.Query["returnTo"].ToString())
        });
        return new EmptyResult();
    }

    [HttpGet("callback")]
    public IActionResult Callback() => Unauthorized(new { error = new { code = "HTTP_401", message = "Sign-in could not be completed." } });

    [HttpGet("me")]
    public async Task<IActionResult> GetCurrentUser() => Ok(await auth.ViewAsync(HttpContext));

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await auth.LogoutAsync(HttpContext);
        return Ok(new { loggedOut = true });
    }
}
