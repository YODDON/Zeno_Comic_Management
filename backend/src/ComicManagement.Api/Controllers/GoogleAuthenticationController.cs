using ComicManagement.Api.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using System.Security.Claims;
using ComicManagement.Identity.Authentication;

namespace ComicManagement.Api.Controllers;

[ApiController]
[Route("api/v1/auth/google")]
public sealed class GoogleAuthenticationController(
    IAntiforgery antiforgery,
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
        try
        {
            await antiforgery.ValidateRequestAsync(HttpContext);
        }
        catch (AntiforgeryValidationException)
        {
            var problemWriter = HttpContext.RequestServices
                .GetRequiredService<ApiProblemWriter>();
            await problemWriter.WriteAsync(
                HttpContext,
                StatusCodes.Status400BadRequest,
                "VALIDATION_FAILED",
                "Validation failed",
                "The CSRF token is missing or invalid.");
            return new EmptyResult();
        }

        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        await logoutHandler.HandleAsync(userId, cancellationToken);
        await HttpContext.SignOutAsync(AuthenticationConstants.SessionScheme);

        return NoContent();
    }
}
