using System.Net;
using ComicManagement.ProviderVerification;
using Xunit;

namespace ComicManagement.ProviderVerification.UnitTests;

public sealed class ProviderProbeTests
{
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Protected_request_only_accepts_explicit_denial_statuses(HttpStatusCode status)
    {
        using var client = Client(_ => new HttpResponseMessage(status));

        var result = await ProviderProbe.ProbeAsync(client, "https://provider.invalid/a", false);

        Assert.True(result.IsExpected);
        Assert.Equal(ProviderProbe.VerifiedDenied, result.Outcome);
        Assert.Equal((int)status, result.HttpStatus);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Provider_failure_cannot_be_misclassified_as_access_denial(HttpStatusCode status)
    {
        using var client = Client(_ => new HttpResponseMessage(status));

        var result = await ProviderProbe.ProbeAsync(client, "https://provider.invalid/a", false);

        Assert.False(result.IsExpected);
        Assert.Equal(ProviderProbe.ProviderUnavailable, result.Outcome);
    }

    [Fact]
    public async Task Network_exception_is_inconclusive()
    {
        using var client = Client(_ => throw new HttpRequestException("network unavailable"));

        var result = await ProviderProbe.ProbeAsync(client, "https://provider.invalid/a", false);

        Assert.False(result.IsExpected);
        Assert.Equal(ProviderProbe.ProviderUnavailable, result.Outcome);
        Assert.Null(result.HttpStatus);
    }

    [Fact]
    public async Task Signed_request_requires_successful_raster_response()
    {
        using var client = Client(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Content.Headers.ContentType = new("image/png");
            return response;
        });

        var result = await ProviderProbe.ProbeAsync(client, "https://provider.invalid/a", true);

        Assert.True(result.IsExpected);
        Assert.Equal(ProviderProbe.VerifiedAccessible, result.Outcome);
    }

    [Fact]
    public void Signed_success_cannot_mask_an_inconclusive_unsigned_probe()
    {
        var probes = new Dictionary<string, ProviderProbeResult>
        {
            ["unsigned"] = new(ProviderProbe.ProviderUnavailable, 503, false, null),
            ["guessed"] = new(ProviderProbe.VerifiedDenied, 404, true, null),
            ["tampered"] = new(ProviderProbe.VerifiedDenied, 403, true, null),
            ["signedInternal"] = new(ProviderProbe.VerifiedAccessible, 200, true, "image/png"),
        };

        Assert.False(CloudinaryVerification.IsPassed(
            new Dictionary<string, bool> { ["authenticatedUpload"] = true },
            probes,
            null));
    }

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> handler) =>
        new(new StubHandler(handler));

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(handler(request));
    }
}
