using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ComicManagement.ProviderVerification;
using Xunit;

namespace ComicManagement.ProviderVerification.UnitTests;

public sealed class EvidencePolicyTests
{
    [Fact]
    public void Required_configuration_rejects_whitespace()
    {
        const string name = "Q02_TEST_WHITESPACE_SECRET";
        var original = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, "   ");
            Assert.Throws<VerificationConfigurationException>(() =>
                VerificationConfiguration.Required(name));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, original);
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("121")]
    [InlineData("not-a-number")]
    public void Http_timeout_is_bounded(string value)
    {
        Assert.Throws<VerificationConfigurationException>(() =>
            VerificationConfiguration.HttpTimeout(value));
    }

    [Fact]
    public void Repository_local_path_is_rejected()
    {
        var root = CreateRepositoryRoot();
        try
        {
            Assert.Throws<VerificationConfigurationException>(() =>
                EvidencePathPolicy.Resolve(Path.Combine(root, "evidence.json"), root));
            Assert.Throws<VerificationConfigurationException>(() =>
                EvidencePathPolicy.Resolve(Path.Combine(root, "nested", "evidence.json"), root));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void External_path_is_allowed()
    {
        var root = CreateRepositoryRoot();
        var external = Path.Combine(Path.GetTempPath(), $"q02-external-{Guid.NewGuid():N}.json");
        try
        {
            Assert.Equal(Path.GetFullPath(external), EvidencePathPolicy.Resolve(external, root));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Evidence_result_has_no_raw_or_unkeyed_identifier_digest()
    {
        const string orderId = "00000000-0000-0000-0000-000000000042";
        const string providerTransactionId = "123456";
        var result = SePayDatabaseVerifier.Verify(
            SePayVerificationScenario.InvalidSignature,
            Observation(orderExists: true, orderStatus: "Pending"),
            401);
        var json = JsonSerializer.Serialize(result);
        var orderDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(orderId)))
            .ToLowerInvariant()[..12];
        var providerDigest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(providerTransactionId)))
            .ToLowerInvariant()[..12];

        Assert.DoesNotContain(orderId, json, StringComparison.Ordinal);
        Assert.DoesNotContain(providerTransactionId, json, StringComparison.Ordinal);
        Assert.DoesNotContain(orderDigest, json, StringComparison.Ordinal);
        Assert.DoesNotContain(providerDigest, json, StringComparison.Ordinal);
    }

    private static string CreateRepositoryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"q02-repo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "ComicManagement.slnx"), "<Solution />");
        return root;
    }

    private static SePayDatabaseObservation Observation(bool orderExists, string orderStatus) =>
        new(false, null, null, false, 0, 0, orderExists, orderStatus, "Pending", "NotStarted",
            0, 0, 0, 0, 10_000);
}
