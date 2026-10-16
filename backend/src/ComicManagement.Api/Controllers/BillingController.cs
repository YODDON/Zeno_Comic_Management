using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json.Serialization;
using ComicManagement.Api.Authentication;
using ComicManagement.Api.Authorization;
using ComicManagement.Api.Billing;
using ComicManagement.Billing.Payments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ComicManagement.Api.Controllers;

[ApiController]
[Route("api/v1/billing")]
[Authorize(Policy = AuthorizationPolicies.User)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class BillingController(TopUpOrderHandler handler) : ControllerBase
{
    [HttpPost("topups")]
    [EnableRateLimiting(BillingRateLimitConstants.TopUpCreatePolicy)]
    [ProducesResponseType<CreateTopUpResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<CreateTopUpResponse>> CreateTopUp(
        CreateTopUpRequest request,
        [FromHeader(Name = "Idempotency-Key"), Required, StringLength(
            PaymentOrder.MaximumIdempotencyKeyLength,
            MinimumLength = 1)]
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await handler.CreateAsync(
            CurrentUserId(),
            request.AmountVnd,
            idempotencyKey,
            cancellationToken);
        if (result.Outcome == TopUpOrderOutcome.ValidationFailed)
        {
            return BadRequest(CreateProblem(
                StatusCodes.Status400BadRequest,
                "VALIDATION_FAILED",
                "Validation failed",
                "The TopUp amount or Idempotency-Key is invalid."));
        }

        if (result.Outcome == TopUpOrderOutcome.IdempotencyConflict)
        {
            return Conflict(CreateProblem(
                StatusCodes.Status409Conflict,
                "IDEMPOTENCY_CONFLICT",
                "Idempotency conflict",
                "The Idempotency-Key was already used with a different request."));
        }

        var state = result.State ?? throw new InvalidOperationException(
            "A successful TopUp order command requires state.");
        return StatusCode(
            StatusCodes.Status201Created,
            new CreateTopUpResponse(
                state.OrderId,
                state.OrderCode,
                state.AmountVnd,
                state.PaidCoinsExpected,
                Enum.Parse<TopUpOrderStatus>(state.Status),
                state.ExpiresAt,
                new PaymentInstructionsResponse(PaymentInstructionType.ProviderSpecific)));
    }

    private Guid CurrentUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private ApiErrorResponse CreateProblem(
        int status,
        string errorCode,
        string title,
        string detail) => HttpContext.RequestServices
        .GetRequiredService<ApiProblemWriter>()
        .Create(HttpContext, status, errorCode, title, detail);
}

public sealed class CreateTopUpRequest
{
    [Range(TopUpAmountPolicy.MinimumAmountVnd, TopUpAmountPolicy.MaximumAmountVnd)]
    public long AmountVnd { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<TopUpOrderStatus>))]
public enum TopUpOrderStatus
{
    Pending,
    Expired,
    Succeeded,
}

[JsonConverter(typeof(JsonStringEnumConverter<PaymentInstructionType>))]
public enum PaymentInstructionType
{
    ProviderSpecific,
}

public sealed record PaymentInstructionsResponse(PaymentInstructionType Type);

public sealed record CreateTopUpResponse(
    Guid OrderId,
    string OrderCode,
    long AmountVnd,
    long PaidCoinsExpected,
    TopUpOrderStatus Status,
    DateTimeOffset ExpiresAt,
    PaymentInstructionsResponse PaymentInstructions);
