using System.Security.Claims;
using ComicManagement.Api.Authentication;
using ComicManagement.Api.Authorization;
using ComicManagement.Identity.Profiles;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Antiforgery;

namespace ComicManagement.Api.Controllers;

[ApiController]
[Route("api/v1/me")]
[Authorize(Policy = AuthorizationPolicies.User)]
public sealed class MeController(
    IAntiforgery antiforgery,
    UpdateDisplayNameHandler updateDisplayNameHandler) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<MeResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<MeResponse> Get()
    {
        SetAntiforgeryToken();
        Response.Headers.CacheControl = "no-store";

        return Ok(CreateResponse(User));
    }

    [HttpPatch]
    [ProducesResponseType<MeResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<MeResponse>> Update(
        UpdateDisplayNameRequest request,
        CancellationToken cancellationToken)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var result = await updateDisplayNameHandler.HandleAsync(
            userId,
            request.DisplayName,
            cancellationToken);

        if (result.Outcome == UpdateDisplayNameOutcome.ValidationFailed)
        {
            var problem = CreateProblem(
                StatusCodes.Status400BadRequest,
                "VALIDATION_FAILED",
                "Validation failed",
                "The display name is required and cannot exceed 120 characters.");
            problem.Extensions["errors"] = new Dictionary<string, string[]>
            {
                ["displayName"] =
                ["Enter a display name containing at most 120 characters."],
            };
            return BadRequest(problem);
        }

        if (result.Outcome == UpdateDisplayNameOutcome.UserNotFound)
        {
            return Unauthorized(CreateProblem(
                StatusCodes.Status401Unauthorized,
                "AUTH_REQUIRED",
                "Authentication required",
                "The signed-in user no longer exists."));
        }

        if (result.Outcome == UpdateDisplayNameOutcome.ConcurrencyConflict)
        {
            return Conflict(CreateProblem(
                StatusCodes.Status409Conflict,
                "CONCURRENCY_CONFLICT",
                "Conflict",
                "The profile changed concurrently. Reload it and try again."));
        }

        var principal = User;
        if (result.SessionRefreshRequired)
        {
            principal = LocalPrincipalFactory.Create(
                result.User ?? throw new InvalidOperationException("Updated user is missing."));
            var currentSession = await HttpContext.AuthenticateAsync(
                AuthenticationConstants.SessionScheme);
            var sessionProperties = currentSession.Properties ?? new AuthenticationProperties();
            sessionProperties.IsPersistent = false;
            await HttpContext.SignInAsync(
                AuthenticationConstants.SessionScheme,
                principal,
                sessionProperties);
            HttpContext.User = principal;
        }

        SetAntiforgeryToken();
        Response.Headers.CacheControl = "no-store";

        return Ok(CreateResponse(principal));
    }

    private void SetAntiforgeryToken()
    {
        var antiforgeryTokens = antiforgery.GetAndStoreTokens(HttpContext);
        Response.Headers[AuthenticationConstants.AntiforgeryHeaderName] =
            antiforgeryTokens.RequestToken;
    }

    private ProblemDetails CreateProblem(
        int status,
        string errorCode,
        string title,
        string detail) =>
        HttpContext.RequestServices.GetRequiredService<ApiProblemWriter>()
            .Create(HttpContext, status, errorCode, title, detail);

    private static MeResponse CreateResponse(ClaimsPrincipal principal)
    {
        var id = Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);

        return new MeResponse(
            id,
            principal.FindFirstValue(ClaimTypes.Name)!,
            principal.FindFirstValue(ClaimTypes.Email),
            principal.FindFirstValue(SessionClaimTypes.AvatarUrl),
            principal.FindFirstValue(ClaimTypes.Role)!,
            principal.FindFirstValue(SessionClaimTypes.Status)!);
    }
}

public sealed record UpdateDisplayNameRequest(string? DisplayName);

public sealed record MeResponse(
    Guid Id,
    string DisplayName,
    string? Email,
    string? AvatarUrl,
    string Role,
    string Status);
