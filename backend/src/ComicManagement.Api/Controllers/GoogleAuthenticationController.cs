using ComicManagement.Api.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Security.Claims;
using ComicManagement.Identity.Authentication;

namespace ComicManagement.Api.Controllers;

[ApiController]
[Route("api/v1/auth/google")]
public sealed class GoogleAuthenticationController(
    LogoutHandler logoutHandler) : ControllerBase
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

    [Authorize]
    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        await logoutHandler.HandleAsync(userId, cancellationToken);
        await HttpContext.SignOutAsync(AuthenticationConstants.SessionScheme);

        return NoContent();
    }
}
