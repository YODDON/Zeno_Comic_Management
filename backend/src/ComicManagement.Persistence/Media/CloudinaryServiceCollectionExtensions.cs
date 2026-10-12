using CloudinaryDotNet;
using ComicManagement.Catalog.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace ComicManagement.Persistence;

public static class CloudinaryServiceCollectionExtensions
{
    public static IServiceCollection AddCloudinaryChapterPageAssets(
        this IServiceCollection services,
        Action<CloudinaryOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services
            .AddOptions<CloudinaryOptions>()
            .Configure(configure)
            .Validate(options => !string.IsNullOrWhiteSpace(options.CloudName), "CloudName is required.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.ApiKey), "ApiKey is required.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.ApiSecret), "ApiSecret is required.")
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.ChapterPagePublicIdPrefix),
                "ChapterPagePublicIdPrefix is required.")
            .ValidateOnStart();

        services.TryAddSingleton<ICloudinaryUploadApi>(serviceProvider =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<CloudinaryOptions>>().Value;
            return new Cloudinary(new Account(options.CloudName, options.ApiKey, options.ApiSecret));
        });
        services.TryAddSingleton<IChapterPageAssetStore, CloudinaryChapterPageAssetStore>();

        return services;
    }
}
