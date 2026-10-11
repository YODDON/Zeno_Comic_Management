using System.Security.Claims;
using ComicManagement.Api.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ComicManagement.Api.Controllers;

[ApiController]
[Route("api/v1/me")]
[Authorize(AuthenticationSchemes = AuthenticationConstants.SessionScheme)]
public sealed class MeController : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<MeResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<MeResponse> Get()
    {
        var id = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        return Ok(new MeResponse(
            id,
            User.FindFirstValue(ClaimTypes.Name)!,
            User.FindFirstValue(ClaimTypes.Email),
            User.FindFirstValue(SessionClaimTypes.AvatarUrl),
            User.FindFirstValue(ClaimTypes.Role)!,
            User.FindFirstValue(SessionClaimTypes.Status)!));
    }
}

public sealed record MeResponse(
    Guid Id,
    string DisplayName,
    string? Email,
    string? AvatarUrl,
    string Role,
    string Status);
