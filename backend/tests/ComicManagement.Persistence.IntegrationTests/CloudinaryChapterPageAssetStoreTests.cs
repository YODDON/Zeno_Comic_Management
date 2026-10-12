using System.Reflection;
using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using ComicManagement.Catalog.Media;
using ComicManagement.Persistence;
using Microsoft.Extensions.Options;
using Xunit;

namespace ComicManagement.Persistence.IntegrationTests;

public sealed class CloudinaryChapterPageAssetStoreTests
{
    [Fact]
    public async Task Upload_uses_authenticated_deterministic_non_overwriting_asset_and_returns_no_url()
    {
        var proxy = CreateProxy();
        proxy.UploadHandler = (_, _) => Task.FromResult(new ImageUploadResult
        {
            PublicId = "comic-management/chapter-pages/provider-id",
            Width = 1200,
            Height = 1800,
            SecureUrl = new Uri("https://provider.example/must-not-be-returned"),
        });
        var store = CreateStore(proxy);
        var chapterId = Guid.NewGuid();
        var pageId = Guid.NewGuid();
        await using var content = new MemoryStream([1, 2, 3]);

        var asset = await store.UploadAsync(new ChapterPageAssetUpload(
            chapterId,
            pageId,
            "page.webp",
            "image/webp",
            content));

        Assert.Equal("comic-management/chapter-pages/provider-id", asset.PublicId);
        Assert.Equal(1200, asset.Width);
        Assert.Equal(1800, asset.Height);
        Assert.DoesNotContain(
            typeof(ChapterPageAsset).GetProperties(),
            property => property.Name.Contains("Url", StringComparison.OrdinalIgnoreCase));

        var parameters = Assert.IsType<ImageUploadParams>(proxy.UploadParameters);
        Assert.Equal("authenticated", parameters.Type);
        Assert.False(parameters.Overwrite);
        Assert.False(parameters.UseFilename);
        Assert.True(parameters.DiscardOriginalFilename);
        Assert.Equal(
            $"comic-management/chapter-pages/{chapterId:N}/{pageId:N}",
            parameters.PublicId);
        Assert.Equal("page.webp", parameters.File.FileName);
        Assert.Same(content, parameters.File.Stream);
    }

    [Fact]
    public async Task Upload_maps_provider_failure_without_exposing_provider_details()
    {
        var proxy = CreateProxy();
        proxy.UploadHandler = (_, _) => Task.FromException<ImageUploadResult>(
            new HttpRequestException("api_secret=must-not-leak"));
        var store = CreateStore(proxy);
        await using var content = new MemoryStream([1]);

        var exception = await Assert.ThrowsAsync<ChapterPageAssetException>(() =>
            store.UploadAsync(new ChapterPageAssetUpload(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "page.png",
                "image/png",
                content)));

        Assert.Equal(ChapterPageAssetErrorCodes.ProviderUnavailable, exception.ErrorCode);
        Assert.DoesNotContain("must-not-leak", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Upload_rejects_incomplete_provider_metadata()
    {
        var proxy = CreateProxy();
        proxy.UploadHandler = (_, _) => Task.FromResult(new ImageUploadResult
        {
            PublicId = "provider-id",
            Width = 0,
            Height = 1800,
        });
        var store = CreateStore(proxy);
        await using var content = new MemoryStream([1]);

        var exception = await Assert.ThrowsAsync<ChapterPageAssetException>(() =>
            store.UploadAsync(new ChapterPageAssetUpload(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "page.png",
                "image/png",
                content)));

        Assert.Equal(ChapterPageAssetErrorCodes.InvalidProviderResponse, exception.ErrorCode);
    }

    [Theory]
    [InlineData("ok")]
    [InlineData("not found")]
    public async Task Delete_is_authenticated_invalidating_and_idempotent(string providerResult)
    {
        var proxy = CreateProxy();
        proxy.DeleteHandler = _ => Task.FromResult(new DeletionResult { Result = providerResult });
        var store = CreateStore(proxy);

        await store.DeleteAsync("comic-management/chapter-pages/provider-id");

        var parameters = Assert.IsType<DeletionParams>(proxy.DeleteParameters);
        Assert.Equal("authenticated", parameters.Type);
        Assert.Equal(ResourceType.Image, parameters.ResourceType);
        Assert.True(parameters.Invalidate);
        Assert.Equal("comic-management/chapter-pages/provider-id", parameters.PublicId);
    }

    [Fact]
    public async Task Delete_maps_unsuccessful_result_to_cleanup_error()
    {
        var proxy = CreateProxy();
        proxy.DeleteHandler = _ => Task.FromResult(new DeletionResult { Result = "pending" });
        var store = CreateStore(proxy);

        var exception = await Assert.ThrowsAsync<ChapterPageAssetException>(() =>
            store.DeleteAsync("comic-management/chapter-pages/provider-id"));

        Assert.Equal(ChapterPageAssetErrorCodes.CleanupFailed, exception.ErrorCode);
    }

    private static CloudinaryChapterPageAssetStore CreateStore(RecordingCloudinaryProxy proxy) =>
        new(
            proxy.Client,
            Options.Create(new CloudinaryOptions
            {
                CloudName = "test-cloud",
                ApiKey = "test-key",
                ApiSecret = "test-secret",
                ChapterPagePublicIdPrefix = "comic-management/chapter-pages",
            }));

    private static RecordingCloudinaryProxy CreateProxy()
    {
        var client = DispatchProxy.Create<ICloudinaryUploadApi, RecordingCloudinaryProxy>();
        var proxy = (RecordingCloudinaryProxy)(object)client;
        proxy.Client = client;
        return proxy;
    }

    public class RecordingCloudinaryProxy : DispatchProxy
    {
        public ICloudinaryUploadApi Client { get; set; } = null!;

        public Func<ImageUploadParams, CancellationToken, Task<ImageUploadResult>>? UploadHandler { get; set; }

        public Func<DeletionParams, Task<DeletionResult>>? DeleteHandler { get; set; }

        public ImageUploadParams? UploadParameters { get; private set; }

        public DeletionParams? DeleteParameters { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            ArgumentNullException.ThrowIfNull(args);

            if (targetMethod.Name == nameof(ICloudinaryUploadApi.UploadAsync) &&
                args[0] is ImageUploadParams uploadParameters)
            {
                UploadParameters = uploadParameters;
                var cancellationToken = args[1] is CancellationToken token ? token : default;
                return (UploadHandler ?? throw new InvalidOperationException("Upload handler is required."))(
                    uploadParameters,
                    cancellationToken);
            }

            if (targetMethod.Name == nameof(ICloudinaryUploadApi.DestroyAsync) &&
                args[0] is DeletionParams deletionParameters)
            {
                DeleteParameters = deletionParameters;
                return (DeleteHandler ?? throw new InvalidOperationException("Delete handler is required."))(
                    deletionParameters);
            }

            throw new NotSupportedException(targetMethod.Name);
        }
    }
}
