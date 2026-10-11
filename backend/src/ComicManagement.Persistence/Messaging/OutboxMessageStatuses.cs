namespace ComicManagement.Persistence;

internal static class OutboxMessageStatuses
{
    internal const string Pending = "Pending";
    internal const string Processing = "Processing";
    internal const string Completed = "Completed";
    internal const string Failed = "Failed";
}
