namespace ComicManagement.Shared.Time;

public static class PostgreSqlTimestamp
{
    private const long TicksPerMicrosecond = 10;

    public static DateTimeOffset Normalize(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "PostgreSQL timestamps must use the UTC offset.",
                nameof(value));
        }

        return new DateTimeOffset(
            value.Ticks - value.Ticks % TicksPerMicrosecond,
            TimeSpan.Zero);
    }
}
