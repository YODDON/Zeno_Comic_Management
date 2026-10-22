using System.Security.Cryptography;
using ComicManagement.Billing.Payments;
using ComicManagement.Billing.Refunds;
using Microsoft.EntityFrameworkCore;

namespace ComicManagement.Persistence;

internal sealed class RefundFinancialProcessingLoader(
    AppDbContext dbContext,
    IRefundRecipientProtector recipientProtector) :
    IRefundFinancialProcessingLoader
{
    public async Task<RefundFinancialLoadResult> LoadForFinancialProcessingAsync(
        Guid refundRequestId,
        CancellationToken cancellationToken = default)
    {
        if (refundRequestId == Guid.Empty)
        {
            return new RefundFinancialLoadResult(RefundFinancialLoadOutcome.NotFound);
        }

        var request = await dbContext.RefundRequests.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == refundRequestId, cancellationToken);
        if (request is null)
        {
            return new RefundFinancialLoadResult(RefundFinancialLoadOutcome.NotFound);
        }

        var authoritative = await (
                from order in dbContext.PaymentOrders.AsNoTracking()
                join lot in dbContext.PaidCoinLots.AsNoTracking()
                    on order.Id equals lot.TopUpOrderId
                join recipient in dbContext.RefundRecipients.AsNoTracking()
                    on request.Id equals recipient.RefundRequestId
                where order.Id == request.PaymentOrderId
                select new { Order = order, Lot = lot, Recipient = recipient })
            .SingleOrDefaultAsync(cancellationToken);
        if (authoritative is null ||
            authoritative.Order.UserId != request.UserId ||
            authoritative.Order.OrderType != PaymentOrderTypes.TopUp ||
            authoritative.Order.AmountVnd != request.OriginalAmountVnd ||
            authoritative.Order.PaidCoinsSnapshot is not { } paidCoinsSnapshot ||
            paidCoinsSnapshot != request.RequiredRecoveryCoins ||
            authoritative.Lot.UserId != request.UserId ||
            authoritative.Lot.TopUpOrderId != request.PaymentOrderId ||
            authoritative.Lot.TopUpOrderType != PaymentOrderTypes.TopUp ||
            authoritative.Lot.GrantedCoins != request.RequiredRecoveryCoins ||
            authoritative.Recipient.RefundRequestId != request.Id)
        {
            return new RefundFinancialLoadResult(RefundFinancialLoadOutcome.IntegrityConflict);
        }

        UnprotectedRefundRecipient unprotected;
        try
        {
            unprotected = recipientProtector.Unprotect(
                authoritative.Recipient.AccountNumberEncrypted,
                authoritative.Recipient.AccountHolderEncrypted,
                authoritative.Recipient.EncryptionKeyVersion);
        }
        catch (Exception exception) when (exception is CryptographicException or
                                           FormatException or
                                           InvalidOperationException or
                                           ArgumentException)
        {
            return new RefundFinancialLoadResult(RefundFinancialLoadOutcome.IntegrityConflict);
        }

        if (!RecipientMatches(authoritative.Recipient, unprotected))
        {
            return new RefundFinancialLoadResult(RefundFinancialLoadOutcome.IntegrityConflict);
        }

        return new RefundFinancialLoadResult(
            RefundFinancialLoadOutcome.Succeeded,
            new RefundFinancialProcessingState(
                MapRequest(request, authoritative.Recipient),
                request.UserId,
                unprotected,
                authoritative.Recipient.EncryptionKeyVersion,
                authoritative.Lot.Id,
                authoritative.Order.OrderType,
                authoritative.Order.OrderStatus,
                authoritative.Order.ProviderPaymentStatus,
                authoritative.Order.FulfillmentStatus,
                authoritative.Order.AmountVnd,
                paidCoinsSnapshot,
                authoritative.Lot.GrantedCoins,
                authoritative.Lot.SpentCoins,
                authoritative.Lot.RefundedCoins,
                authoritative.Lot.ReservedCoins));
    }

    private static bool RecipientMatches(
        RefundRecipient recipient,
        UnprotectedRefundRecipient unprotected)
    {
        var accountNumber = unprotected.AccountNumber?.Trim() ?? string.Empty;
        var accountHolder = string.Join(' ',
                (unprotected.AccountHolder ?? string.Empty)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToUpperInvariant();
        return accountNumber.Length is >= 4 and <= RefundRecipient.MaximumAccountNumberLength &&
               accountNumber.All(char.IsAsciiDigit) &&
               accountHolder.Length is >= 2 and <= RefundRecipient.MaximumAccountHolderLength &&
               accountNumber.EndsWith(recipient.AccountNumberLast4, StringComparison.Ordinal) &&
               MaskAccountNumber(accountNumber) == recipient.AccountNumberMasked &&
               MaskAccountHolder(accountHolder) == recipient.AccountHolderMasked;
    }

    private static string MaskAccountNumber(string accountNumber) =>
        new string('*', Math.Max(0, accountNumber.Length - 4)) + accountNumber[^4..];

    private static string MaskAccountHolder(string accountHolder) => string.Join(' ',
        accountHolder.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => string.Concat(part[0], "***")));

    private static RefundRequestState MapRequest(
        RefundRequest request,
        RefundRecipient recipient) => new(
            request.Id,
            request.PaymentOrderId,
            request.Status,
            request.Reason,
            request.OriginalAmountVnd,
            request.FeeAmountVnd,
            request.NetRefundVnd,
            request.RequiredRecoveryCoins,
            request.RequestedAt,
            recipient.BankCode,
            recipient.AccountNumberMasked,
            recipient.AccountHolderMasked);
}
