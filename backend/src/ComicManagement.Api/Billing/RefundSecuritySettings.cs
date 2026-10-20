using System.Security.Cryptography;
using System.Text;
using ComicManagement.Billing.Refunds;

namespace ComicManagement.Api.Billing;

internal sealed class RefundSecuritySettings
{
    internal const string SectionName = "Billing:RefundSecurity";

    internal required string CurrentFingerprintVersion { get; init; }

    internal required IReadOnlyDictionary<string, byte[]> FingerprintKeys { get; init; }

    internal required string CurrentProtectionVersion { get; init; }

    internal static RefundSecuritySettings FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        var currentFingerprintVersion = NormalizeVersion(
            section["CurrentFingerprintVersion"],
            "Billing:RefundSecurity:CurrentFingerprintVersion");
        var currentProtectionVersion = NormalizeVersion(
            section["CurrentProtectionVersion"],
            "Billing:RefundSecurity:CurrentProtectionVersion");
        var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var child in section.GetSection("FingerprintKeys").GetChildren())
        {
            var version = NormalizeVersion(
                child.Key,
                $"Billing:RefundSecurity:FingerprintKeys:{child.Key}");
            byte[] key;
            try
            {
                key = Convert.FromBase64String(child.Value ?? string.Empty);
            }
            catch (FormatException exception)
            {
                throw new InvalidOperationException(
                    $"Billing:RefundSecurity:FingerprintKeys:{version} must be Base64.",
                    exception);
            }

            if (key.Length < 32)
            {
                throw new InvalidOperationException(
                    $"Billing:RefundSecurity:FingerprintKeys:{version} must contain at least 32 bytes.");
            }

            keys.Add(version, key);
        }

        if (!keys.ContainsKey(currentFingerprintVersion))
        {
            throw new InvalidOperationException(
                "The current refund fingerprint version must have a configured key.");
        }

        return new RefundSecuritySettings
        {
            CurrentFingerprintVersion = currentFingerprintVersion,
            CurrentProtectionVersion = currentProtectionVersion,
            FingerprintKeys = keys,
        };
    }

    private static string NormalizeVersion(string? value, string settingName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > RefundRequest.MaximumSecurityVersionLength ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new InvalidOperationException(
                $"{settingName} must be 1-{RefundRequest.MaximumSecurityVersionLength} ASCII letters, digits, '-' or '_'.");
        }

        return value;
    }
}

internal sealed class RefundRequestFingerprintService(RefundSecuritySettings settings) :
    IRefundRequestFingerprintService
{
    public RefundRequestFingerprints CreateFingerprints(string canonicalRequest)
    {
        ArgumentNullException.ThrowIfNull(canonicalRequest);
        var payload = Encoding.UTF8.GetBytes(canonicalRequest);
        var candidates = settings.FingerprintKeys.ToDictionary(
            pair => pair.Key,
            pair => Convert.ToHexString(HMACSHA256.HashData(pair.Value, payload)).ToLowerInvariant(),
            StringComparer.Ordinal);
        return new RefundRequestFingerprints(
            settings.CurrentFingerprintVersion,
            candidates[settings.CurrentFingerprintVersion],
            candidates);
    }
}
