namespace ComicManagement.Catalog.Chapters;

public static class ChapterPublicationStatuses
{
    public const string Draft = "Draft";
    public const string Published = "Published";
    public const string Hidden = "Hidden";

    public static bool IsDefined(string value) => value is Draft or Published or Hidden;
}
