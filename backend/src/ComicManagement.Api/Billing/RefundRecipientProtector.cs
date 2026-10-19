using System.Text;
using ComicManagement.Billing.Refunds;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;

namespace ComicManagement.Api.Billing;

internal sealed class RefundRecipientProtector(IDataProtectionProvider provider) :
    IRefundRecipientProtector
{
    private readonly IDataProtector _accountNumberProtector = provider.CreateProtector(
        "ComicManagement.Billing.RefundRecipient.AccountNumber.v1");
    private readonly IDataProtector _accountHolderProtector = provider.CreateProtector(
        "ComicManagement.Billing.RefundRecipient.AccountHolder.v1");

    public ProtectedRefundRecipient Protect(string accountNumber, string accountHolder)
    {
        var protectedAccountNumber = _accountNumberProtector.Protect(accountNumber);
        var protectedAccountHolder = _accountHolderProtector.Protect(accountHolder);
        var accountKeyId = ReadDataProtectionKeyId(protectedAccountNumber);
        var holderKeyId = ReadDataProtectionKeyId(protectedAccountHolder);
        if (accountKeyId != holderKeyId)
        {
            throw new InvalidOperationException(
                "Recipient fields must be protected with the same active data-protection key.");
        }

        return new ProtectedRefundRecipient(
            Encoding.UTF8.GetBytes(protectedAccountNumber),
            Encoding.UTF8.GetBytes(protectedAccountHolder),
            $"aspnet-dp:{accountKeyId:N}");
    }

    private static Guid ReadDataProtectionKeyId(string protectedValue)
    {
        var payload = WebEncoders.Base64UrlDecode(protectedValue);
        const int magicHeaderLength = 4;
        const int keyIdLength = 16;
        if (payload.Length < magicHeaderLength + keyIdLength)
        {
            throw new InvalidOperationException(
                "Data protection returned an invalid recipient payload.");
        }

        return new Guid(payload.AsSpan(magicHeaderLength, keyIdLength));
    }
}
