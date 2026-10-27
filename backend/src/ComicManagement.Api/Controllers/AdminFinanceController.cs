using System.ComponentModel.DataAnnotations;
using ComicManagement.Administration.Finance;
using ComicManagement.Api.Authentication;
using ComicManagement.Api.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ComicManagement.Api.Controllers;

[ApiController]
[Route("api/v1/admin")]
[Authorize(Policy = AuthorizationPolicies.Admin)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminFinanceController(
    FinanceAdministrationQueryHandler queries) : ControllerBase
{
    [HttpGet("payments")]
    [ProducesResponseType<AdminFinancePage<AdminPaymentListItem>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<AdminFinancePage<AdminPaymentListItem>>> ListPayments(
        [FromQuery, StringLength(FinanceAdministrationQueryHandler.MaximumSearchLength)] string? search,
        [FromQuery] string? status,
        [FromQuery] string? type,
        [FromQuery, Range(1, int.MaxValue)] int page = 1,
        [FromQuery, Range(1, FinanceAdministrationQueryHandler.MaximumPageSize)] int pageSize =
            FinanceAdministrationQueryHandler.DefaultPageSize,
        CancellationToken cancellationToken = default) =>
        ToListResponse(await queries.ListPaymentsAsync(
            search, status, type, page, pageSize, cancellationToken));

    [HttpGet("payments/{paymentId:guid}")]
    [ProducesResponseType<AdminPaymentDetail>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AdminPaymentDetail>> GetPayment(
        Guid paymentId,
        CancellationToken cancellationToken = default) =>
        ToItemResponse(await queries.GetPaymentAsync(paymentId, cancellationToken));

    [HttpGet("ledger")]
    [ProducesResponseType<AdminFinancePage<AdminLedgerItem>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<AdminFinancePage<AdminLedgerItem>>> ListLedger(
        [FromQuery, StringLength(FinanceAdministrationQueryHandler.MaximumSearchLength)] string? search,
        [FromQuery] string? operationType,
        [FromQuery, Range(1, int.MaxValue)] int page = 1,
        [FromQuery, Range(1, FinanceAdministrationQueryHandler.MaximumPageSize)] int pageSize =
            FinanceAdministrationQueryHandler.DefaultPageSize,
        CancellationToken cancellationToken = default) =>
        ToListResponse(await queries.ListLedgerAsync(
            search, operationType, page, pageSize, cancellationToken));

    private ActionResult<T> ToListResponse<T>(FinanceAdministrationQueryResult<T> result)
    {
        Response.Headers.CacheControl = "no-store";
        return result.Outcome == FinanceAdministrationQueryOutcome.Succeeded && result.Value is not null
            ? Ok(result.Value)
            : BadRequest(Problem("The finance list criteria are invalid."));
    }

    private ActionResult<T> ToItemResponse<T>(FinanceAdministrationQueryResult<T> result)
    {
        Response.Headers.CacheControl = "no-store";
        return result.Outcome switch
        {
            FinanceAdministrationQueryOutcome.Succeeded when result.Value is not null => Ok(result.Value),
            FinanceAdministrationQueryOutcome.NotFound => NotFound(HttpContext.RequestServices
                .GetRequiredService<ApiProblemWriter>().Create(
                    HttpContext, 404, "RESOURCE_NOT_FOUND", "Resource not found",
                    "The requested finance record was not found.")),
            _ => BadRequest(Problem("The finance record identifier is invalid.")),
        };
    }

    private ApiErrorResponse Problem(string detail) => HttpContext.RequestServices
        .GetRequiredService<ApiProblemWriter>().Create(
            HttpContext, 400, "VALIDATION_FAILED", "Validation failed", detail);
}
