using System.Text;
using ComicManagement.Billing.Refunds;
using Microsoft.AspNetCore.DataProtection;

namespace ComicManagement.Api.Billing;

internal sealed class RefundRecipientProtector(
    IDataProtectionProvider provider,
    RefundSecuritySettings settings) :
    IRefundRecipientProtector
{
    public ProtectedRefundRecipient Protect(string accountNumber, string accountHolder)
    {
        var version = settings.CurrentProtectionVersion;
        var protectedAccountNumber = CreateProtector("AccountNumber", version).Protect(accountNumber);
        var protectedAccountHolder = CreateProtector("AccountHolder", version).Protect(accountHolder);

        return new ProtectedRefundRecipient(
            Encoding.UTF8.GetBytes(protectedAccountNumber),
            Encoding.UTF8.GetBytes(protectedAccountHolder),
            version);
    }

    public UnprotectedRefundRecipient Unprotect(
        byte[] accountNumberCiphertext,
        byte[] accountHolderCiphertext,
        string protectionVersion) => new(
            CreateProtector("AccountNumber", protectionVersion).Unprotect(
                Encoding.UTF8.GetString(accountNumberCiphertext)),
            CreateProtector("AccountHolder", protectionVersion).Unprotect(
                Encoding.UTF8.GetString(accountHolderCiphertext)));

    private IDataProtector CreateProtector(string field, string version) =>
        provider.CreateProtector($"ComicManagement.Billing.RefundRecipient.{field}.{version}");
}
