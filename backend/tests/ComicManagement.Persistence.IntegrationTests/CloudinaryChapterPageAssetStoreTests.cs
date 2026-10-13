using System.Reflection;
using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using ComicManagement.Catalog.Media;
using ComicManagement.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace ComicManagement.Persistence.IntegrationTests;

public sealed class CloudinaryChapterPageAssetStoreTests
{
    [Fact]
    public async Task Registration_composes_the_scoped_upload_handler()
    {
        await using var serviceProvider = new ServiceCollection()
            .AddPersistence("Host=localhost;Database=unused;Username=unused;Password=unused")
            .AddCloudinaryChapterPageAssets(options =>
            {
                options.CloudName = "test-cloud";
                options.ApiKey = "test-key";
                options.ApiSecret = "test-secret";
                options.MaximumChapterPageBytes = 10 * 1024 * 1024;
            })
            .BuildServiceProvider(validateScopes: true);
        await using var scope = serviceProvider.CreateAsyncScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<UploadChapterPageHandler>());
    }

    [Fact]
    public async Task Upload_uses_authenticated_deterministic_non_overwriting_asset_and_returns_no_url()
    {
        var uploadProxy = CreateUploadProxy();
        var adminProxy = CreateAdminProxy();
        var chapterId = Guid.NewGuid();
        var pageId = Guid.NewGuid();
        var expectedPublicId = PublicId(chapterId, pageId);
        uploadProxy.UploadHandler = (_, _) => Task.FromResult(UploadResult(expectedPublicId));
        var store = CreateStore(uploadProxy, adminProxy);
        await using var content = new MemoryStream([1, 2, 3]);

        var asset = await store.UploadAsync(new ChapterPageAssetUpload(
            chapterId,
            pageId,
            "page.webp",
            "image/webp",
            content));

        Assert.Equal(expectedPublicId, asset.PublicId);
        Assert.Equal(1200, asset.Width);
        Assert.Equal(1800, asset.Height);
        Assert.DoesNotContain(
            typeof(ChapterPageAsset).GetProperties(),
            property => property.Name.Contains("Url", StringComparison.OrdinalIgnoreCase));

        var parameters = Assert.IsType<ImageUploadParams>(uploadProxy.UploadParameters);
        Assert.Equal("authenticated", parameters.Type);
        Assert.False(parameters.Overwrite);
        Assert.False(parameters.UseFilename);
        Assert.True(parameters.DiscardOriginalFilename);
        Assert.Equal(expectedPublicId, parameters.PublicId);
        Assert.Equal("page.webp", parameters.File.FileName);
        Assert.Same(content, parameters.File.Stream);
        Assert.Null(adminProxy.GetParameters);
    }

    [Fact]
    public async Task Upload_recovers_an_existing_asset_after_ambiguous_provider_failure()
    {
        var uploadProxy = CreateUploadProxy();
        var adminProxy = CreateAdminProxy();
        var chapterId = Guid.NewGuid();
        var pageId = Guid.NewGuid();
        var expectedPublicId = PublicId(chapterId, pageId);
        uploadProxy.UploadHandler = (_, _) => Task.FromException<ImageUploadResult>(
            new HttpRequestException("response lost after upload"));
        adminProxy.GetHandler = (_, _) => Task.FromResult(ResourceResult(expectedPublicId));
        var store = CreateStore(uploadProxy, adminProxy);
        await using var content = new MemoryStream([1]);

        var asset = await store.UploadAsync(new ChapterPageAssetUpload(
            chapterId,
            pageId,
            "page.png",
            "image/png",
            content));

        Assert.Equal(expectedPublicId, asset.PublicId);
        var parameters = Assert.IsType<GetResourceParams>(adminProxy.GetParameters);
        Assert.Equal(expectedPublicId, parameters.PublicId);
        Assert.Equal("authenticated", parameters.Type);
        Assert.Equal(ResourceType.Image, parameters.ResourceType);
    }

    [Fact]
    public async Task Upload_maps_unrecoverable_failure_without_retaining_provider_secrets()
    {
        var uploadProxy = CreateUploadProxy();
        var adminProxy = CreateAdminProxy();
        uploadProxy.UploadHandler = (_, _) => Task.FromException<ImageUploadResult>(
            new HttpRequestException("api_secret=must-not-leak"));
        adminProxy.GetHandler = (_, _) => Task.FromException<GetResourceResult>(
            new InvalidOperationException("api_secret=also-must-not-leak"));
        var store = CreateStore(uploadProxy, adminProxy);
        await using var content = new MemoryStream([1]);

        var exception = await Assert.ThrowsAsync<ChapterPageAssetException>(() =>
            store.UploadAsync(new ChapterPageAssetUpload(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "page.png",
                "image/png",
                content)));

        Assert.Equal(ChapterPageAssetErrorCodes.ProviderUnavailable, exception.ErrorCode);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("must-not-leak", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Upload_rejects_inconsistent_provider_identity_or_metadata()
    {
        var uploadProxy = CreateUploadProxy();
        uploadProxy.UploadHandler = (_, _) => Task.FromResult(UploadResult(
            "comic-management/chapter-pages/different-asset",
            width: 0));
        var store = CreateStore(uploadProxy, CreateAdminProxy());
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

    [Fact]
    public async Task Upload_maps_null_provider_response_to_stable_error()
    {
        var uploadProxy = CreateUploadProxy();
        var adminProxy = CreateAdminProxy();
        uploadProxy.UploadHandler = (_, _) => Task.FromResult<ImageUploadResult>(null!);
        adminProxy.GetHandler = (_, _) => Task.FromResult<GetResourceResult>(null!);
        var store = CreateStore(uploadProxy, adminProxy);
        await using var content = new MemoryStream([1]);

        var exception = await Assert.ThrowsAsync<ChapterPageAssetException>(() =>
            store.UploadAsync(new ChapterPageAssetUpload(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "page.png",
                "image/png",
                content)));

        Assert.Equal(ChapterPageAssetErrorCodes.ProviderRejected, exception.ErrorCode);
    }

    [Fact]
    public void Upload_validation_rejects_content_over_the_configured_limit()
    {
        var store = CreateStore(CreateUploadProxy(), CreateAdminProxy(), maximumChapterPageBytes: 2);
        using var content = new MemoryStream([1, 2, 3]);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            store.ValidateUpload(new ChapterPageAssetUpload(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "page.png",
                "image/png",
                content)));

        Assert.Contains("2 bytes", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ok")]
    [InlineData("not found")]
    public async Task Delete_is_authenticated_invalidating_and_idempotent(string providerResult)
    {
        var uploadProxy = CreateUploadProxy();
        uploadProxy.DeleteHandler = _ => Task.FromResult(new DeletionResult { Result = providerResult });
        var store = CreateStore(uploadProxy, CreateAdminProxy());

        await store.DeleteAsync("comic-management/chapter-pages/provider-id");

        var parameters = Assert.IsType<DeletionParams>(uploadProxy.DeleteParameters);
        Assert.Equal("authenticated", parameters.Type);
        Assert.Equal(ResourceType.Image, parameters.ResourceType);
        Assert.True(parameters.Invalidate);
        Assert.Equal("comic-management/chapter-pages/provider-id", parameters.PublicId);
    }

    [Fact]
    public async Task Delete_rejects_assets_outside_the_configured_namespace()
    {
        var store = CreateStore(CreateUploadProxy(), CreateAdminProxy());

        await Assert.ThrowsAsync<ArgumentException>(() => store.DeleteAsync("covers/not-a-chapter-page"));
    }

    [Fact]
    public async Task Delete_maps_unsuccessful_result_to_cleanup_error()
    {
        var uploadProxy = CreateUploadProxy();
        uploadProxy.DeleteHandler = _ => Task.FromResult(new DeletionResult { Result = "pending" });
        var store = CreateStore(uploadProxy, CreateAdminProxy());

        var exception = await Assert.ThrowsAsync<ChapterPageAssetException>(() =>
            store.DeleteAsync("comic-management/chapter-pages/provider-id"));

        Assert.Equal(ChapterPageAssetErrorCodes.CleanupFailed, exception.ErrorCode);
    }

    private static CloudinaryChapterPageAssetStore CreateStore(
        RecordingUploadProxy uploadProxy,
        RecordingAdminProxy adminProxy,
        long maximumChapterPageBytes = 10 * 1024 * 1024) => new(
            uploadProxy.Client,
            adminProxy.Client,
            Options.Create(new CloudinaryOptions
            {
                CloudName = "test-cloud",
                ApiKey = "test-key",
                ApiSecret = "test-secret",
                ChapterPagePublicIdPrefix = "comic-management/chapter-pages",
                MaximumChapterPageBytes = maximumChapterPageBytes,
            }));

    private static RecordingUploadProxy CreateUploadProxy()
    {
        var client = DispatchProxy.Create<ICloudinaryUploadApi, RecordingUploadProxy>();
        var proxy = (RecordingUploadProxy)(object)client;
        proxy.Client = client;
        return proxy;
    }

    private static RecordingAdminProxy CreateAdminProxy()
    {
        var client = DispatchProxy.Create<ICloudinaryAdminApi, RecordingAdminProxy>();
        var proxy = (RecordingAdminProxy)(object)client;
        proxy.Client = client;
        return proxy;
    }

    private static string PublicId(Guid chapterId, Guid pageId) =>
        $"comic-management/chapter-pages/{chapterId:N}/{pageId:N}";

    private static ImageUploadResult UploadResult(string publicId, int width = 1200) => new()
    {
        PublicId = publicId,
        Width = width,
        Height = 1800,
        Type = "authenticated",
        ResourceType = "image",
        SecureUrl = new Uri("https://provider.example/must-not-be-returned"),
    };

    private static GetResourceResult ResourceResult(string publicId) => new()
    {
        PublicId = publicId,
        Width = 1200,
        Height = 1800,
        Type = "authenticated",
        SecureUrl = "https://provider.example/must-not-be-returned",
    };

    public class RecordingUploadProxy : DispatchProxy
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

    public class RecordingAdminProxy : DispatchProxy
    {
        public ICloudinaryAdminApi Client { get; set; } = null!;

        public Func<GetResourceParams, CancellationToken, Task<GetResourceResult>>? GetHandler { get; set; }

        public GetResourceParams? GetParameters { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            ArgumentNullException.ThrowIfNull(args);

            if (targetMethod.Name == nameof(ICloudinaryAdminApi.GetResourceAsync) &&
                args[0] is GetResourceParams parameters)
            {
                GetParameters = parameters;
                var cancellationToken = args[1] is CancellationToken token ? token : default;
                return (GetHandler ?? throw new InvalidOperationException("Get handler is required."))(
                    parameters,
                    cancellationToken);
            }

            throw new NotSupportedException(targetMethod.Name);
        }
    }
}
