namespace ComicManagement.Catalog.Chapters;

public static class ChapterPageAssetStatuses
{
    public const string Pending = "Pending";
    public const string Ready = "Ready";

    public static bool IsDefined(string value) => value is Pending or Ready;
}
