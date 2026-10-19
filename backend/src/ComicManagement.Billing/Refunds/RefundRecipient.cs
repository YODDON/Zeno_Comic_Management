namespace ComicManagement.Billing.Refunds;

public sealed class RefundRecipient
{
    public const int MaximumBankCodeLength = 32;
    public const int MaximumAccountNumberLength = 34;
    public const int MaximumAccountHolderLength = 120;
    public const int MaximumMaskLength = 160;
    public const int MaximumKeyVersionLength = 80;

    private RefundRecipient()
    {
    }

    public Guid Id { get; private set; }

    public Guid RefundRequestId { get; private set; }

    public string BankCode { get; private set; } = string.Empty;

    public byte[] AccountNumberEncrypted { get; private set; } = [];

    public byte[] AccountHolderEncrypted { get; private set; } = [];

    public string AccountNumberLast4 { get; private set; } = string.Empty;

    public string AccountNumberMasked { get; private set; } = string.Empty;

    public string AccountHolderMasked { get; private set; } = string.Empty;

    public string EncryptionKeyVersion { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public static RefundRecipient Create(
        Guid id,
        Guid refundRequestId,
        string bankCode,
        byte[] accountNumberEncrypted,
        byte[] accountHolderEncrypted,
        string accountNumberLast4,
        string accountNumberMasked,
        string accountHolderMasked,
        string encryptionKeyVersion,
        DateTimeOffset createdAt)
    {
        EnsureIdentifier(id, nameof(id));
        EnsureIdentifier(refundRequestId, nameof(refundRequestId));
        EnsureText(bankCode, MaximumBankCodeLength, nameof(bankCode));
        EnsureCiphertext(accountNumberEncrypted, nameof(accountNumberEncrypted));
        EnsureCiphertext(accountHolderEncrypted, nameof(accountHolderEncrypted));
        EnsureText(accountNumberLast4, 4, nameof(accountNumberLast4));
        EnsureText(accountNumberMasked, MaximumMaskLength, nameof(accountNumberMasked));
        EnsureText(accountHolderMasked, MaximumMaskLength, nameof(accountHolderMasked));
        EnsureText(encryptionKeyVersion, MaximumKeyVersionLength, nameof(encryptionKeyVersion));
        if (createdAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must use the UTC offset.", nameof(createdAt));
        }

        return new RefundRecipient
        {
            Id = id,
            RefundRequestId = refundRequestId,
            BankCode = bankCode,
            AccountNumberEncrypted = [.. accountNumberEncrypted],
            AccountHolderEncrypted = [.. accountHolderEncrypted],
            AccountNumberLast4 = accountNumberLast4,
            AccountNumberMasked = accountNumberMasked,
            AccountHolderMasked = accountHolderMasked,
            EncryptionKeyVersion = encryptionKeyVersion,
            CreatedAt = createdAt,
        };
    }

    private static void EnsureIdentifier(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("An identifier is required.", parameterName);
        }
    }

    private static void EnsureText(string value, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value != value.Trim() || value.Length > maximumLength)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static void EnsureCiphertext(byte[] value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length == 0)
        {
            throw new ArgumentException("Protected recipient data is required.", parameterName);
        }
    }
}
