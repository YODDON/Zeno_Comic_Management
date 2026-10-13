namespace ComicManagement.Catalog.Media;

public sealed class ChapterPageAssetException : Exception
{
    public ChapterPageAssetException(string errorCode, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}

public static class ChapterPageAssetErrorCodes
{
    public const string ProviderRejected = "MEDIA_UPLOAD_REJECTED";
    public const string ProviderUnavailable = "MEDIA_PROVIDER_UNAVAILABLE";
    public const string InvalidProviderResponse = "MEDIA_INVALID_PROVIDER_RESPONSE";
    public const string CleanupFailed = "MEDIA_CLEANUP_FAILED";
}
