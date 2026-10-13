using CloudinaryDotNet;
using ComicManagement.Reading.Access;

namespace ComicManagement.Persistence;

internal sealed class CloudinaryFreeChapterPageDeliveryUrlProvider(Cloudinary cloudinary)
    : IChapterPageDeliveryUrlProvider
{
    public bool TryCreateFreeDeliveryUrl(string assetKey, out string deliveryUrl)
    {
        deliveryUrl = string.Empty;
        if (string.IsNullOrWhiteSpace(assetKey))
        {
            return false;
        }

        try
        {
            var candidate = cloudinary.Api.UrlImgUp
                .Secure(true)
                .Signed(true)
                .Type("authenticated")
                .BuildUrl(assetKey);
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps)
            {
                return false;
            }

            deliveryUrl = candidate;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
