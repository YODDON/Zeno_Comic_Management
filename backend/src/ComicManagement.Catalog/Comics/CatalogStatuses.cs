namespace ComicManagement.Catalog.Comics;

public static class ComicPublicationStatuses
{
    public const string Draft = "Draft";
    public const string Published = "Published";
    public const string Hidden = "Hidden";

    public static bool IsDefined(string value) => value is Draft or Published or Hidden;
}

public static class ComicSeriesStatuses
{
    public const string Ongoing = "Ongoing";
    public const string Completed = "Completed";
    public const string Hiatus = "Hiatus";

    public static bool IsDefined(string value) => value is Ongoing or Completed or Hiatus;
}
