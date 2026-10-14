using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using ComicManagement.Api.Authentication;
using ComicManagement.Api.Authorization;
using ComicManagement.Wallet.Queries;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ComicManagement.Api.Controllers;

[ApiController]
[Route("api/v1/wallet")]
[Authorize(Policy = AuthorizationPolicies.User)]
public sealed class WalletController(WalletQueryHandler queryHandler) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<WalletBalanceState>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<WalletBalanceState>> GetBalance(
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await queryHandler.GetBalanceAsync(CurrentUserId(), cancellationToken);
        return result.Outcome == WalletQueryOutcome.Succeeded
            ? Ok(result.Balance)
            : BadRequest(ValidationProblem("The authenticated user identifier is invalid."));
    }

    [HttpGet("entries")]
    [ProducesResponseType<WalletEntryPage>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<WalletEntryPage>> ListEntries(
        [FromQuery, StringLength(WalletQueryHandler.CursorLength, MinimumLength = WalletQueryHandler.CursorLength)]
        string? cursor,
        CancellationToken cancellationToken,
        [FromQuery, Range(1, WalletQueryHandler.MaximumPageSize)] int limit =
            WalletQueryHandler.DefaultPageSize)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await queryHandler.ListEntriesAsync(
            CurrentUserId(),
            cursor,
            limit,
            cancellationToken);
        return result.Outcome == WalletQueryOutcome.Succeeded
            ? Ok(result.Page)
            : BadRequest(ValidationProblem("Cursor or limit is invalid."));
    }

    private Guid CurrentUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private ApiErrorResponse ValidationProblem(string detail) =>
        HttpContext.RequestServices.GetRequiredService<ApiProblemWriter>()
            .Create(
                HttpContext,
                StatusCodes.Status400BadRequest,
                "VALIDATION_FAILED",
                "Validation failed",
                detail);
}
