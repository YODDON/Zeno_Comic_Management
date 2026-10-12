namespace ComicManagement.Persistence;

public sealed class CloudinaryOptions
{
    public string CloudName { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public string ApiSecret { get; set; } = string.Empty;

    public string ChapterPagePublicIdPrefix { get; set; } = "comic-management/chapter-pages";
}
