using System.Text.Json;
using ComicManagement.Billing.Events;
using ComicManagement.Missions.Periods;
using ComicManagement.Missions.Receipts;
using ComicManagement.Reading.Events;
using ComicManagement.Shared.Messaging;
using ComicManagement.Shared.Time;
using Microsoft.EntityFrameworkCore;

namespace ComicManagement.Persistence;

internal sealed class MissionActionReceiptRecorder(
    AppDbContext dbContext,
    MissionPeriodResolver periodResolver,
    IClock clock)
{
    internal async Task RecordAsync(
        Guid sourceEventId,
        string sourceEventType,
        Guid userId,
        Guid? subjectId,
        DateTimeOffset occurredAt,
        DateTimeOffset envelopeOccurredAt,
        CancellationToken cancellationToken)
    {
        if (occurredAt != envelopeOccurredAt)
        {
            throw new InvalidDataException(
                "Mission event payload timestamp does not match its outbox envelope.");
        }

        var receivedAt = clock.UtcNow;
        var localDate = periodResolver.GetLocalDate(occurredAt);
        var receipt = ActionReceipt.Create(
            Guid.NewGuid(),
            sourceEventId,
            sourceEventType,
            userId,
            subjectId,
            occurredAt,
            localDate,
            receivedAt);

        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO missions.action_receipts
                (id, source_event_id, source_event_type, user_id, subject_id,
                 occurred_at, occurred_local_date, received_at, processing_status)
            VALUES
                ({receipt.Id}, {receipt.SourceEventId}, {receipt.SourceEventType},
                 {receipt.UserId}, {receipt.SubjectId}, {receipt.OccurredAt},
                 {receipt.OccurredLocalDate}, {receipt.ReceivedAt},
                 {receipt.ProcessingStatus})
            ON CONFLICT (source_event_id, source_event_type) DO NOTHING;
            """, cancellationToken);
    }
}

internal abstract class MissionActionReceiptHandler<TPayload>(
    MissionActionReceiptRecorder recorder) : IOutboxMessageHandler
    where TPayload : class
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    public abstract string ConsumerName { get; }

    public abstract string EventType { get; }

    public async Task HandleAsync(
        OutboxMessageEnvelope message,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<TPayload>(
            message.Payload,
            SerializerOptions) ?? throw new InvalidDataException(
                $"The {message.EventType} payload is invalid.");
        var action = Map(message, payload);
        await recorder.RecordAsync(
            action.SourceEventId,
            message.EventType,
            action.UserId,
            action.SubjectId,
            action.OccurredAt,
            message.OccurredAt,
            cancellationToken);
    }

    protected abstract MissionReceiptInput Map(
        OutboxMessageEnvelope envelope,
        TPayload payload);
}

internal sealed record MissionReceiptInput(
    Guid SourceEventId,
    Guid UserId,
    Guid? SubjectId,
    DateTimeOffset OccurredAt);

internal sealed class ChapterReadQualifiedMissionReceiptHandler(
    MissionActionReceiptRecorder recorder) :
    MissionActionReceiptHandler<ChapterReadQualified>(recorder)
{
    public override string ConsumerName => "Missions.ChapterReadQualified.v1";

    public override string EventType => ReadingEventTypes.ChapterReadQualified;

    protected override MissionReceiptInput Map(
        OutboxMessageEnvelope envelope,
        ChapterReadQualified payload) => new(
            payload.SessionId,
            payload.UserId,
            payload.ChapterId,
            payload.QualifiedAt);
}

internal sealed class ComicFollowedMissionReceiptHandler(
    MissionActionReceiptRecorder recorder) :
    MissionActionReceiptHandler<ComicFollowed>(recorder)
{
    public override string ConsumerName => "Missions.ComicFollowed.v1";

    public override string EventType => ReadingEventTypes.ComicFollowed;

    protected override MissionReceiptInput Map(
        OutboxMessageEnvelope envelope,
        ComicFollowed payload) => new(
            envelope.Id,
            payload.UserId,
            payload.ComicId,
            payload.FollowedAt);
}

internal sealed class ChapterUnlockedMissionReceiptHandler(
    MissionActionReceiptRecorder recorder) :
    MissionActionReceiptHandler<ChapterUnlocked>(recorder)
{
    public override string ConsumerName => "Missions.ChapterUnlocked.v1";

    public override string EventType => ReadingEventTypes.ChapterUnlocked;

    protected override MissionReceiptInput Map(
        OutboxMessageEnvelope envelope,
        ChapterUnlocked payload) => new(
            payload.UnlockId,
            payload.UserId,
            payload.ChapterId,
            payload.UnlockedAt);
}

internal sealed class WalletTopUpCompletedMissionReceiptHandler(
    MissionActionReceiptRecorder recorder) :
    MissionActionReceiptHandler<WalletTopUpCompletedEvent>(recorder)
{
    public override string ConsumerName => "Missions.WalletTopUpCompleted.v1";

    public override string EventType => BillingEventTypes.WalletTopUpCompleted;

    protected override MissionReceiptInput Map(
        OutboxMessageEnvelope envelope,
        WalletTopUpCompletedEvent payload) => new(
            payload.PaymentOrderId,
            payload.UserId,
            payload.PaymentOrderId,
            payload.FulfilledAt);
}
