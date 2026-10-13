using System.Data;
using ComicManagement.Reading.Access;
using ComicManagement.Reading.Events;
using ComicManagement.Reading.Sessions;
using ComicManagement.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace ComicManagement.Persistence;

internal sealed class ReadingSessionStore(
    AppDbContext dbContext,
    IOutboxWriter outboxWriter) : IReadingSessionStore
{
    public async Task<ReadingSessionCommandResult> CreateAsync(
        Guid sessionId,
        Guid userId,
        Guid chapterId,
        ReadingQualificationPolicy policy,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        var manifest = await ChapterAccessQuery.LoadManifestAsync(
            dbContext,
            chapterId,
            cancellationToken);
        var accessFailure = GetAccessFailure(manifest, userId);
        if (accessFailure is not null)
        {
            return accessFailure;
        }

        var source = manifest ?? throw new InvalidOperationException(
            "An accessible chapter requires a manifest.");
        var session = ReadingSession.Create(
            sessionId,
            userId,
            source.Access.ComicId,
            chapterId,
            source.Revision,
            source.Pages.Count,
            policy,
            startedAt);
        dbContext.ReadingSessions.Add(session);

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Succeeded(session);
    }

    public async Task<ReadingSessionCommandResult> UpdateProgressAsync(
        Guid sessionId,
        Guid userId,
        int currentPageOrder,
        decimal currentPageOffsetRatio,
        long sequenceNumber,
        string requestFingerprint,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        var session = await LockSessionAsync(sessionId, userId, cancellationToken);
        if (session is null)
        {
            return NotFound();
        }

        var sequence = session.ClassifySequence(sequenceNumber, requestFingerprint);
        if (sequence.Outcome == ReadingHeartbeatOutcome.Replayed)
        {
            return Succeeded(session);
        }

        if (sequence.Outcome == ReadingHeartbeatOutcome.SequenceConflict)
        {
            return SequenceConflict(sequence.ConflictReason);
        }

        if (session.IsExpiredAt(receivedAt))
        {
            return Expired();
        }

        var manifest = await ChapterAccessQuery.LoadManifestAsync(
            dbContext,
            session.ChapterId,
            cancellationToken);
        var accessFailure = GetAccessFailure(manifest, userId);
        if (accessFailure is not null)
        {
            return accessFailure;
        }

        var source = manifest ?? throw new InvalidOperationException(
            "An accessible chapter requires a manifest.");
        var readyPageOrders = source.Pages.Select(page => page.PageOrder).ToArray();
        var heartbeat = session.ApplyHeartbeat(
            currentPageOrder,
            currentPageOffsetRatio,
            sequenceNumber,
            requestFingerprint,
            source.Revision,
            readyPageOrders,
            receivedAt);
        if (heartbeat.Outcome == ReadingHeartbeatOutcome.Expired)
        {
            return Expired();
        }

        if (heartbeat.Outcome == ReadingHeartbeatOutcome.SequenceConflict)
        {
            return SequenceConflict(heartbeat.ConflictReason);
        }

        if (readyPageOrders.Contains(currentPageOrder))
        {
            await UpsertProgressAsync(
                session,
                currentPageOrder,
                currentPageOffsetRatio,
                receivedAt,
                cancellationToken);
        }
        else if (heartbeat.PositionRemapped &&
                 session.CurrentPageOrder.HasValue &&
                 session.CurrentPageOffsetRatio.HasValue)
        {
            await UpsertProgressAsync(
                session,
                session.CurrentPageOrder.Value,
                session.CurrentPageOffsetRatio.Value,
                receivedAt,
                cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Succeeded(session);
    }

    public async Task<ReadingSessionCommandResult> QualifyAsync(
        Guid sessionId,
        Guid userId,
        DateTimeOffset qualifiedAt,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        var session = await LockSessionAsync(sessionId, userId, cancellationToken);
        if (session is null)
        {
            return NotFound();
        }

        if (session.QualifiedAt.HasValue)
        {
            return Succeeded(session);
        }

        var manifest = await ChapterAccessQuery.LoadManifestAsync(
            dbContext,
            session.ChapterId,
            cancellationToken);
        var accessFailure = GetAccessFailure(manifest, userId);
        if (accessFailure is not null)
        {
            return accessFailure;
        }

        var currentManifest = manifest ?? throw new InvalidOperationException(
            "An accessible chapter requires a manifest.");
        var readyPageOrders = currentManifest.Pages
            .Select(page => page.PageOrder)
            .ToArray();
        var manifestSynchronization = session.SynchronizeManifest(
            currentManifest.Revision,
            readyPageOrders);
        if (manifestSynchronization.PositionRemapped &&
            session.CurrentPageOrder.HasValue &&
            session.CurrentPageOffsetRatio.HasValue)
        {
            await UpsertProgressAsync(
                session,
                session.CurrentPageOrder.Value,
                session.CurrentPageOffsetRatio.Value,
                qualifiedAt,
                cancellationToken);
        }

        var qualification = session.Qualify(qualifiedAt);
        if (qualification == ReadingQualificationOutcome.Expired)
        {
            await PersistManifestSynchronizationAsync(
                manifestSynchronization,
                transaction,
                cancellationToken);
            return NotQualified(session, "SESSION_EXPIRED");
        }

        if (qualification == ReadingQualificationOutcome.ProgressThresholdNotMet)
        {
            await PersistManifestSynchronizationAsync(
                manifestSynchronization,
                transaction,
                cancellationToken);
            return NotQualified(session, "PROGRESS_THRESHOLD_NOT_MET");
        }

        if (qualification == ReadingQualificationOutcome.ActiveDurationThresholdNotMet)
        {
            await PersistManifestSynchronizationAsync(
                manifestSynchronization,
                transaction,
                cancellationToken);
            return NotQualified(session, "ACTIVE_DURATION_THRESHOLD_NOT_MET");
        }

        if (qualification != ReadingQualificationOutcome.Qualified)
        {
            return Succeeded(session);
        }

        if (!manifestSynchronization.PositionRemapped &&
            session.CurrentPageOrder.HasValue &&
            session.CurrentPageOffsetRatio.HasValue &&
            currentManifest.Pages.Any(page => page.PageOrder == session.CurrentPageOrder.Value))
        {
            await UpsertProgressAsync(
                session,
                session.CurrentPageOrder.Value,
                session.CurrentPageOffsetRatio.Value,
                qualifiedAt,
                cancellationToken);
        }

        outboxWriter.Enqueue(
            ReadingEventTypes.ChapterReadQualified,
            new ChapterReadQualified(
                session.Id,
                session.UserId,
                session.ComicId,
                session.ChapterId,
                session.QualifiedAt ?? throw new InvalidOperationException(
                    "A qualified session requires qualifiedAt."),
                session.PolicyRevision,
                session.MinimumProgressBasisPoints,
                session.MinimumActiveDurationMs,
                session.ManifestRevision),
            session.QualifiedAt.Value);

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Succeeded(session);
    }

    private async Task PersistManifestSynchronizationAsync(
        ReadingManifestSynchronization synchronization,
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (!synchronization.Changed)
        {
            return;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<ReadingSession?> LockSessionAsync(
        Guid sessionId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var sessions = await dbContext.ReadingSessions
            .FromSqlInterpolated(
                $"SELECT * FROM reading.reading_sessions WHERE id = {sessionId} AND user_id = {userId} FOR UPDATE")
            .AsTracking()
            .ToListAsync(cancellationToken);
        return sessions.SingleOrDefault();
    }

    private async Task UpsertProgressAsync(
        ReadingSession session,
        int pageOrder,
        decimal pageOffsetRatio,
        DateTimeOffset readAt,
        CancellationToken cancellationToken)
    {
        var lockKey = $"{session.UserId:N}:{session.ComicId:N}";
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))",
            cancellationToken);

        var rows = await dbContext.ReadingProgress
            .FromSqlInterpolated(
                $"SELECT * FROM reading.reading_progress WHERE user_id = {session.UserId} AND comic_id = {session.ComicId} FOR UPDATE")
            .AsTracking()
            .ToListAsync(cancellationToken);
        var progress = rows.SingleOrDefault();
        if (progress is null)
        {
            dbContext.ReadingProgress.Add(ReadingProgress.Create(
                session.UserId,
                session.ComicId,
                session.ChapterId,
                pageOrder,
                pageOffsetRatio,
                readAt,
                session.Id,
                session.StartedAt));
            return;
        }

        progress.Apply(
            session.ChapterId,
            pageOrder,
            pageOffsetRatio,
            readAt,
            session.Id,
            session.StartedAt);
    }

    private static ReadingSessionCommandResult? GetAccessFailure(
        ChapterAccessManifestRow? manifest,
        Guid userId)
    {
        if (manifest is null)
        {
            return NotFound();
        }

        var access = ChapterAccessPolicy.Resolve(
            ChapterAccessQuery.ToProjection(manifest.Access),
            isAuthenticated: userId != Guid.Empty);
        return access.CanRead
            ? null
            : new ReadingSessionCommandResult(ReadingSessionCommandOutcome.Forbidden);
    }

    private static ReadingSessionCommandResult Succeeded(ReadingSession session) => new(
        ReadingSessionCommandOutcome.Succeeded,
        ToState(session));

    private static ReadingSessionCommandResult NotQualified(
        ReadingSession session,
        string reasonCode) => new(
        ReadingSessionCommandOutcome.Succeeded,
        ToState(session),
        reasonCode);

    private static ReadingSessionCommandResult NotFound() => new(
        ReadingSessionCommandOutcome.NotFound);

    private static ReadingSessionCommandResult SequenceConflict(string? reason) => new(
        ReadingSessionCommandOutcome.SequenceConflict,
        ReasonCode: reason);

    private static ReadingSessionCommandResult Expired(string? reason = null) => new(
        ReadingSessionCommandOutcome.Expired,
        ReasonCode: reason ?? "SESSION_EXPIRED");

    private static ReadingSessionState ToState(ReadingSession session) => new(
        session.Id,
        session.UserId,
        session.ComicId,
        session.ChapterId,
        session.StartedAt,
        session.ExpiresAt,
        session.LastActivityAt,
        session.LastActivityWasReady,
        session.ActiveDurationMs,
        session.CurrentPageOrder,
        session.CurrentPageOffsetRatio,
        session.MaxProgressBasisPoints,
        session.ManifestRevision,
        session.ManifestPageCount,
        session.LastAcceptedSequence,
        session.PolicyRevision,
        session.MinimumProgressBasisPoints,
        session.MinimumActiveDurationMs,
        session.QualifiedAt,
        session.Version);
}
