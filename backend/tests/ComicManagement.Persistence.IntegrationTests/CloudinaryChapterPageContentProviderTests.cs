using System.Net;
using System.Net.Http.Headers;
using CloudinaryDotNet;
using ComicManagement.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ComicManagement.Persistence.IntegrationTests;

public sealed class CloudinaryChapterPageContentProviderTests
{
    [Fact]
    public async Task Owned_raster_asset_is_fetched_over_internal_signed_https_request()
    {
        var handler = new RecordingHttpMessageHandler(_ => Response(
            HttpStatusCode.OK,
            "image/webp",
            [1, 2, 3]));
        using var client = new HttpClient(handler);
        var provider = CreateProvider(client);

        var content = await provider.GetAsync(
            "comic-management/chapter-pages/chapter/page");

        Assert.NotNull(content);
        Assert.Equal([1, 2, 3], content.Bytes);
        Assert.Equal("image/webp", content.ContentType);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(Uri.UriSchemeHttps, request.Scheme);
        Assert.Contains("/authenticated/", request.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("s--", request.AbsolutePath, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("covers/not-owned")]
    [InlineData("comic-management/chapter-pages/../covers/asset")]
    [InlineData("comic-management/chapter-pages\\asset")]
    public async Task Invalid_asset_namespace_is_rejected_without_provider_request(
        string assetKey)
    {
        var handler = new RecordingHttpMessageHandler(_ => Response(
            HttpStatusCode.OK,
            "image/webp",
            [1]));
        using var client = new HttpClient(handler);

        var content = await CreateProvider(client).GetAsync(assetKey);

        Assert.Null(content);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("image/svg+xml", 3, 10)]
    [InlineData("text/html", 3, 10)]
    [InlineData("image/png", 3, 2)]
    public async Task Unsupported_or_oversized_provider_response_fails_closed(
        string contentType,
        int responseBytes,
        long maximumBytes)
    {
        var handler = new RecordingHttpMessageHandler(_ => Response(
            HttpStatusCode.OK,
            contentType,
            Enumerable.Repeat((byte)1, responseBytes).ToArray()));
        using var client = new HttpClient(handler);

        var content = await CreateProvider(client, maximumBytes).GetAsync(
            "comic-management/chapter-pages/chapter/page");

        Assert.Null(content);
    }

    [Fact]
    public async Task Provider_error_fails_closed_without_returning_content()
    {
        var handler = new RecordingHttpMessageHandler(_ =>
            throw new HttpRequestException("api_secret=must-not-leak"));
        using var client = new HttpClient(handler);

        var content = await CreateProvider(client).GetAsync(
            "comic-management/chapter-pages/chapter/page");

        Assert.Null(content);
    }

    private static CloudinaryChapterPageContentProvider CreateProvider(
        HttpClient httpClient,
        long maximumBytes = 10) => new(
        new Cloudinary(new Account("test-cloud", "test-key", "test-secret")),
        httpClient,
        Options.Create(new CloudinaryOptions
        {
            CloudName = "test-cloud",
            ApiKey = "test-key",
            ApiSecret = "test-secret",
            ChapterPagePublicIdPrefix = "comic-management/chapter-pages",
            MaximumChapterPageBytes = maximumBytes,
        }),
        NullLogger<CloudinaryChapterPageContentProvider>.Instance);

    private static HttpResponseMessage Response(
        HttpStatusCode statusCode,
        string contentType,
        byte[] bytes)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new ByteArrayContent(bytes),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return response;
    }

    private sealed class RecordingHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        internal List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(responseFactory(request));
        }
    }
}
