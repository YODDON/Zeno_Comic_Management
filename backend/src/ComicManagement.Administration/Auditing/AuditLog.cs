namespace ComicManagement.Administration.Auditing;

public sealed class AuditLog
{
    public const int MaximumActionLength = 120;
    public const int MaximumTargetTypeLength = 80;
    public const int MaximumCorrelationIdLength = 200;

    private AuditLog()
    {
    }

    public Guid Id { get; private set; }

    public Guid ActorId { get; private set; }

    public string Action { get; private set; } = string.Empty;

    public string TargetType { get; private set; } = string.Empty;

    public Guid TargetId { get; private set; }

    public string CorrelationId { get; private set; } = string.Empty;

    public DateTimeOffset OccurredAt { get; private set; }

    public string RedactedChanges { get; private set; } = "{}";

    public static AuditLog Create(
        Guid id,
        Guid actorId,
        string action,
        string targetType,
        Guid targetId,
        string correlationId,
        DateTimeOffset occurredAt,
        string redactedChanges)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Audit log ID is required.", nameof(id));
        }

        if (actorId == Guid.Empty)
        {
            throw new ArgumentException("Audit actor ID is required.", nameof(actorId));
        }

        ValidateText(action, MaximumActionLength, nameof(action));
        ValidateText(targetType, MaximumTargetTypeLength, nameof(targetType));
        if (targetId == Guid.Empty)
        {
            throw new ArgumentException("Audit target ID is required.", nameof(targetId));
        }

        ValidateText(correlationId, MaximumCorrelationIdLength, nameof(correlationId));
        if (occurredAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must use the UTC offset.", nameof(occurredAt));
        }

        var safeChanges = AuditRedactionPolicy.CanonicalizeForPersistence(redactedChanges);

        return new AuditLog
        {
            Id = id,
            ActorId = actorId,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            CorrelationId = correlationId,
            OccurredAt = occurredAt,
            RedactedChanges = safeChanges,
        };
    }

    private static void ValidateText(string value, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maximumLength)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
