namespace ComicManagement.Shared.Pagination;

public readonly record struct OffsetPage(int Page, int PageSize, int Offset)
{
    public const int MaximumOffset = int.MaxValue;

    public static bool TryCreate(
        int page,
        int pageSize,
        int maximumPageSize,
        out OffsetPage result)
    {
        result = default;
        if (page < 1 || pageSize < 1 || pageSize > maximumPageSize)
        {
            return false;
        }

        var offset = ((long)page - 1L) * pageSize;
        if (offset > MaximumOffset)
        {
            return false;
        }

        result = new OffsetPage(page, pageSize, (int)offset);
        return true;
    }

    public int TotalPages(long totalItems)
    {
        if (totalItems <= 0)
        {
            return 0;
        }

        var totalPages = totalItems / PageSize + (totalItems % PageSize == 0 ? 0 : 1);
        return (int)Math.Min(totalPages, int.MaxValue);
    }
}
