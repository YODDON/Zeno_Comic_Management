using System.ComponentModel.DataAnnotations;
using ComicManagement.Api.Billing;
using ComicManagement.Billing.Payments;
using ComicManagement.Workflows.Billing;
using ComicManagement.Shared.Time;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ComicManagement.Api.Controllers;

[ApiController]
[Route("api/v1/webhooks/sepay/ipn")]
[AllowAnonymous]
[EnableRateLimiting(BillingRateLimitConstants.SePayWebhookPolicy)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class SePayWebhookController(
    TopUpFulfillmentWorkflow workflow,
    SePayWebhookPolicy policy,
    IClock clock) : ControllerBase
{
    [HttpPost]
    [Consumes("application/json")]
    [RequestSizeLimit(1024 * 1024)]
    [ProducesResponseType<SePayWebhookAcknowledgement>(StatusCodes.Status200OK)]
    [ProducesResponseType<SePayWebhookAcknowledgement>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<SePayWebhookAcknowledgement>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<SePayWebhookAcknowledgement>(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType<SePayWebhookAcknowledgement>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<SePayWebhookAcknowledgement>> Receive(
        [FromHeader(Name = SePayWebhookHeaders.Signature)] string? signature,
        [FromHeader(Name = SePayWebhookHeaders.Timestamp)] string? timestamp,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        if (Request.ContentLength > policy.MaximumBodyBytes)
        {
            return StatusCode(
                StatusCodes.Status413PayloadTooLarge,
                SePayWebhookAcknowledgement.Failed);
        }

        var rawBody = await ReadBodyAsync(cancellationToken);
        if (rawBody is null)
        {
            return StatusCode(
                StatusCodes.Status413PayloadTooLarge,
                SePayWebhookAcknowledgement.Failed);
        }

        var result = await workflow.HandleAsync(
            new SePayWebhookRequest(rawBody, signature, timestamp),
            clock.UtcNow,
            cancellationToken);
        return result.Outcome switch
        {
            SePayWebhookOutcome.Accepted => Ok(SePayWebhookAcknowledgement.Succeeded),
            SePayWebhookOutcome.Disabled => StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                SePayWebhookAcknowledgement.Failed),
            SePayWebhookOutcome.Unauthorized => Unauthorized(
                SePayWebhookAcknowledgement.Failed),
            _ => BadRequest(SePayWebhookAcknowledgement.Failed),
        };
    }

    private async Task<byte[]?> ReadBodyAsync(CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await Request.Body.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return stream.ToArray();
            }

            if (stream.Length + read > policy.MaximumBodyBytes)
            {
                return null;
            }

            await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }
}

public sealed record SePayWebhookAcknowledgement(bool Success)
{
    public static readonly SePayWebhookAcknowledgement Succeeded = new(true);

    public static readonly SePayWebhookAcknowledgement Failed = new(false);
}
public sealed class SePayWebhookPayloadContract
{
    [Range(1, long.MaxValue)]
    public long Id { get; init; }

    [Required, StringLength(64, MinimumLength = 1)]
    public string Gateway { get; init; } = string.Empty;

    [Required, RegularExpression("^\\d{4}-\\d{2}-\\d{2} \\d{2}:\\d{2}:\\d{2}$")]
    public string TransactionDate { get; init; } = string.Empty;

    [Required, StringLength(64, MinimumLength = 1)]
    public string AccountNumber { get; init; } = string.Empty;

    [StringLength(64)]
    public string? SubAccount { get; init; }

    [StringLength(64)]
    public string? Code { get; init; }

    [Required, StringLength(1024, MinimumLength = 1)]
    public string Content { get; init; } = string.Empty;

    [Required, RegularExpression("^(in|out)$")]
    public string TransferType { get; init; } = string.Empty;

    [StringLength(1024)]
    public string? Description { get; init; }

    [Range(1, long.MaxValue)]
    public long TransferAmount { get; init; }

    [Range(0, long.MaxValue)]
    public long Accumulated { get; init; }

    [StringLength(128)]
    public string? ReferenceCode { get; init; }
}
