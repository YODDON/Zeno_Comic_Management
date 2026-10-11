namespace ComicManagement.Identity.Users;

public sealed class GoogleIdentity
{
    public const int MaximumSubjectLength = 255;

    private GoogleIdentity()
    {
    }

    public Guid UserId { get; private set; }

    public string GoogleSubject { get; private set; } = string.Empty;

    public DateTimeOffset LinkedAt { get; private set; }

    public User User { get; private set; } = null!;

    public static GoogleIdentity Create(
        Guid userId,
        string googleSubject,
        DateTimeOffset linkedAt)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User ID is required.", nameof(userId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(googleSubject);
        if (googleSubject.Length > MaximumSubjectLength)
        {
            throw new ArgumentOutOfRangeException(nameof(googleSubject));
        }

        EnsureUtc(linkedAt, nameof(linkedAt));

        return new GoogleIdentity
        {
            UserId = userId,
            GoogleSubject = googleSubject,
            LinkedAt = linkedAt,
        };
    }

    private static void EnsureUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must use the UTC offset.", parameterName);
        }
    }
}
