using ComicManagement.Missions.Periods;

namespace ComicManagement.Missions.Receipts;

public static class ActionReceiptStatuses
{
    public const string Pending = "Pending";
    public const string Processed = "Processed";
}

public sealed class ActionReceipt
{
    public const int MaximumSourceEventTypeLength = 200;

    private ActionReceipt()
    {
    }

    public Guid Id { get; private set; }

    public Guid SourceEventId { get; private set; }

    public string SourceEventType { get; private set; } = string.Empty;

    public Guid UserId { get; private set; }

    public Guid? SubjectId { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public DateOnly OccurredLocalDate { get; private set; }

    public DateTimeOffset ReceivedAt { get; private set; }

    public string ProcessingStatus { get; private set; } = string.Empty;

    public static ActionReceipt Create(
        Guid id,
        Guid sourceEventId,
        string sourceEventType,
        Guid userId,
        Guid? subjectId,
        DateTimeOffset occurredAt,
        DateTimeOffset receivedAt)
    {
        if (id == Guid.Empty ||
            sourceEventId == Guid.Empty ||
            string.IsNullOrWhiteSpace(sourceEventType) ||
            sourceEventType.Length > MaximumSourceEventTypeLength ||
            userId == Guid.Empty ||
            subjectId == Guid.Empty ||
            occurredAt.Offset != TimeSpan.Zero ||
            receivedAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Mission action receipt is invalid.");
        }

        return new ActionReceipt
        {
            Id = id,
            SourceEventId = sourceEventId,
            SourceEventType = sourceEventType,
            UserId = userId,
            SubjectId = subjectId,
            OccurredAt = occurredAt,
            OccurredLocalDate = MissionPeriodResolver.GetVietnamLocalDate(occurredAt),
            ReceivedAt = receivedAt,
            ProcessingStatus = ActionReceiptStatuses.Pending,
        };
    }
}
