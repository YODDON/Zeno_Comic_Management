using System.Security.Cryptography;
using System.Text.Json;
using ComicManagement.Billing.Events;
using ComicManagement.Identity.Events;
using ComicManagement.Missions.Receipts;
using ComicManagement.Reading.Events;
using ComicManagement.Shared.Messaging;
using ComicManagement.Shared.Time;
using Microsoft.EntityFrameworkCore;

namespace ComicManagement.Persistence;

internal sealed class MissionActionReceiptRecorder(
    AppDbContext dbContext,
    IClock clock)
{
    internal async Task RecordAsync(
        Guid sourceEventId,
        string sourceEventType,
        Guid userId,
        Guid? subjectId,
        string payloadFingerprint,
        DateTimeOffset occurredAt,
        DateTimeOffset envelopeOccurredAt,
        CancellationToken cancellationToken)
    {
        var canonicalOccurredAt = PostgreSqlTimestamp.Normalize(occurredAt);
        var canonicalEnvelopeOccurredAt = PostgreSqlTimestamp.Normalize(envelopeOccurredAt);
        if (canonicalOccurredAt != canonicalEnvelopeOccurredAt)
        {
            throw new InvalidDataException(
                "Mission event payload timestamp does not match its outbox envelope.");
        }

        var receivedAt = PostgreSqlTimestamp.Normalize(clock.UtcNow);
        var receipt = ActionReceipt.Create(
            Guid.NewGuid(),
            sourceEventId,
            sourceEventType,
            userId,
            subjectId,
            payloadFingerprint,
            canonicalOccurredAt,
            receivedAt);

        var affectedRows = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO missions.action_receipts
                (id, source_event_id, source_event_type, user_id, subject_id,
                 payload_fingerprint,
                 occurred_at, occurred_local_date, received_at, processing_status)
            VALUES
                ({receipt.Id}, {receipt.SourceEventId}, {receipt.SourceEventType},
                 {receipt.UserId}, {receipt.SubjectId}, {receipt.PayloadFingerprint},
                 {receipt.OccurredAt},
                 {receipt.OccurredLocalDate}, {receipt.ReceivedAt},
                 {receipt.ProcessingStatus})
            ON CONFLICT (source_event_id, source_event_type) DO UPDATE
            SET source_event_id = missions.action_receipts.source_event_id
            WHERE missions.action_receipts.user_id = EXCLUDED.user_id
              AND missions.action_receipts.subject_id IS NOT DISTINCT FROM EXCLUDED.subject_id
              AND missions.action_receipts.payload_fingerprint = EXCLUDED.payload_fingerprint
              AND missions.action_receipts.occurred_at = EXCLUDED.occurred_at
              AND missions.action_receipts.occurred_local_date = EXCLUDED.occurred_local_date;
            """, cancellationToken);
        if (affectedRows == 0)
        {
            throw new MissionActionReceiptConflictException();
        }
    }
}

internal sealed class MissionActionReceiptConflictException : InvalidOperationException
{
    internal MissionActionReceiptConflictException()
        : base("A mission action receipt identity was reused with different authoritative data.")
    {
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
        var payloadFingerprint = MissionPayloadFingerprint.Create(payload);
        await recorder.RecordAsync(
            action.SourceEventId,
            message.EventType,
            action.UserId,
            action.SubjectId,
            payloadFingerprint,
            action.OccurredAt,
            message.OccurredAt,
            cancellationToken);
    }

    protected abstract MissionReceiptInput Map(
        OutboxMessageEnvelope envelope,
        TPayload payload);
}

internal static class MissionPayloadFingerprint
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    internal static string Create<TPayload>(TPayload payload)
        where TPayload : class
    {
        ArgumentNullException.ThrowIfNull(payload);
        return Convert.ToHexString(
                SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
                    payload,
                    SerializerOptions)))
            .ToLowerInvariant();
    }
}

internal sealed record MissionReceiptInput(
    Guid SourceEventId,
    Guid UserId,
    Guid? SubjectId,
    DateTimeOffset OccurredAt);

internal sealed class UserLoggedInMissionReceiptHandler(
    MissionActionReceiptRecorder recorder) :
    MissionActionReceiptHandler<UserLoggedIn>(recorder)
{
    public override string ConsumerName => "Missions.UserLoggedIn.v1";

    public override string EventType => IdentityEventTypes.UserLoggedIn;

    protected override MissionReceiptInput Map(
        OutboxMessageEnvelope envelope,
        UserLoggedIn payload) => new(
            payload.LoginId,
            payload.UserId,
            null,
            payload.LoggedInAt);
}

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
