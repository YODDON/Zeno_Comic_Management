using System.Text;
using System.Text.Json;

namespace ComicManagement.Administration.Auditing;

/// <summary>
/// Canonical defense-in-depth policy for audit change documents. Audit writers must
/// never persist sensitive values, and readers apply the same policy so legacy or
/// out-of-band rows cannot disclose them through an API.
/// </summary>
public static class AuditRedactionPolicy
{
    public static string CanonicalizeForPersistence(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var document = JsonDocument.Parse(json);
        return Sanitize(document.RootElement);
    }

    public static string SanitizeForExposure(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "{}";
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return Sanitize(document.RootElement);
        }
        catch (JsonException)
        {
            // Persisted audit content is untrusted at this boundary. Fail closed and
            // do not include the malformed value in an exception or log message.
            return "{}";
        }
    }

    private static string Sanitize(JsonElement root)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteElement(writer, root);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteElement(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()
                             .Where(property => !IsSensitiveKey(property.Name))
                             .OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteElement(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteElement(writer, item);
                }
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static bool IsSensitiveKey(string key)
    {
        var normalized = new string(key
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());

        return normalized.Contains("accountnumber", StringComparison.Ordinal) ||
            normalized.Contains("bankaccount", StringComparison.Ordinal) ||
            normalized.Contains("bankcode", StringComparison.Ordinal) ||
            normalized.Contains("bankreference", StringComparison.Ordinal) ||
            normalized.Contains("evidencereference", StringComparison.Ordinal) ||
            normalized.Contains("accountholder", StringComparison.Ordinal) ||
            normalized.Contains("bankrecipient", StringComparison.Ordinal) ||
            normalized.StartsWith("recipient", StringComparison.Ordinal) ||
            normalized.Contains("ciphertext", StringComparison.Ordinal) ||
            normalized.StartsWith("encrypted", StringComparison.Ordinal) ||
            normalized.StartsWith("protectedrecipient", StringComparison.Ordinal) ||
            normalized.Contains("encryptedpayload", StringComparison.Ordinal) ||
            normalized.Contains("encrypteddata", StringComparison.Ordinal) ||
            normalized is "token" or "password" or "apikey" or "secret" ||
            normalized.EndsWith("token", StringComparison.Ordinal) ||
            normalized.Contains("secret", StringComparison.Ordinal) ||
            normalized.Contains("apikey", StringComparison.Ordinal) ||
            normalized.Contains("password", StringComparison.Ordinal) ||
            normalized.Contains("providerpayload", StringComparison.Ordinal) ||
            normalized.Contains("financialpayload", StringComparison.Ordinal) ||
            normalized.Contains("rawpayload", StringComparison.Ordinal) ||
            normalized.Contains("requestfingerprint", StringComparison.Ordinal);
    }
}
