using ComicManagement.Api.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ComicManagement.Api.Controllers;

[ApiController]
[Route("api/v1/auth/google")]
public sealed class GoogleAuthenticationController : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("login")]
    [EnableRateLimiting(AuthenticationConstants.LoginRateLimitPolicy)]
    public IActionResult Login([FromQuery] string? returnUrl = null)
    {
        var safeReturnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl! : "/";

        return Challenge(
            new AuthenticationProperties
            {
                IsPersistent = false,
                RedirectUri = safeReturnUrl,
            },
            AuthenticationConstants.GoogleScheme);
    }
}
