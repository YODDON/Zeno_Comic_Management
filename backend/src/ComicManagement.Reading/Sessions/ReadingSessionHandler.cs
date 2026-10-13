using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ComicManagement.Shared.Time;

namespace ComicManagement.Reading.Sessions;

public enum ReadingSessionCommandOutcome
{
    Succeeded = 0,
    ValidationFailed = 1,
    NotFound = 2,
    Forbidden = 3,
    SequenceConflict = 4,
    Expired = 5,
}

public sealed record ReadingSessionState(
    Guid SessionId,
    Guid UserId,
    Guid ComicId,
    Guid ChapterId,
    DateTimeOffset StartedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? LastActivityAt,
    bool LastActivityWasReady,
    long ActiveDurationMs,
    int? CurrentPageOrder,
    decimal? CurrentPageOffsetRatio,
    int AcceptedMaxProgressBasisPoints,
    string ManifestRevision,
    int ManifestPageCount,
    long LastAcceptedSequence,
    string PolicyRevision,
    int MinimumProgressBasisPoints,
    long MinimumActiveDurationMs,
    DateTimeOffset? QualifiedAt,
    long Version);

public sealed record ReadingSessionCommandResult(
    ReadingSessionCommandOutcome Outcome,
    ReadingSessionState? State = null,
    string? ReasonCode = null);

public interface IReadingSessionStore
{
    Task<ReadingSessionCommandResult> CreateAsync(
        Guid sessionId,
        Guid userId,
        Guid chapterId,
        ReadingQualificationPolicy policy,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken = default);

    Task<ReadingSessionCommandResult> UpdateProgressAsync(
        Guid sessionId,
        Guid userId,
        int currentPageOrder,
        decimal currentPageOffsetRatio,
        long sequenceNumber,
        string requestFingerprint,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken = default);

    Task<ReadingSessionCommandResult> QualifyAsync(
        Guid sessionId,
        Guid userId,
        DateTimeOffset qualifiedAt,
        CancellationToken cancellationToken = default);
}

public sealed class ReadingSessionHandler(
    IReadingSessionStore store,
    IClock clock)
{
    public Task<ReadingSessionCommandResult> CreateAsync(
        Guid userId,
        Guid chapterId,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty || chapterId == Guid.Empty)
        {
            return ValidationFailed();
        }

        return store.CreateAsync(
            Guid.NewGuid(),
            userId,
            chapterId,
            ReadingQualificationPolicy.R02V1,
            clock.UtcNow,
            cancellationToken);
    }

    public Task<ReadingSessionCommandResult> UpdateProgressAsync(
        Guid sessionId,
        Guid userId,
        int currentPageOrder,
        decimal currentPageOffsetRatio,
        long sequenceNumber,
        CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty ||
            userId == Guid.Empty ||
            currentPageOrder < 1 ||
            currentPageOffsetRatio is < 0 or > 1 ||
            sequenceNumber < 1)
        {
            return ValidationFailed();
        }

        return store.UpdateProgressAsync(
            sessionId,
            userId,
            currentPageOrder,
            currentPageOffsetRatio,
            sequenceNumber,
            CreateFingerprint(currentPageOrder, currentPageOffsetRatio, sequenceNumber),
            clock.UtcNow,
            cancellationToken);
    }

    public Task<ReadingSessionCommandResult> QualifyAsync(
        Guid sessionId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty || userId == Guid.Empty)
        {
            return ValidationFailed();
        }

        return store.QualifyAsync(sessionId, userId, clock.UtcNow, cancellationToken);
    }

    private static string CreateFingerprint(
        int currentPageOrder,
        decimal currentPageOffsetRatio,
        long sequenceNumber)
    {
        var canonicalPayload = string.Create(
            CultureInfo.InvariantCulture,
            $"{currentPageOrder}|{currentPageOffsetRatio:G29}|{sequenceNumber}");
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPayload)));
    }

    private static Task<ReadingSessionCommandResult> ValidationFailed() =>
        Task.FromResult(new ReadingSessionCommandResult(
            ReadingSessionCommandOutcome.ValidationFailed));
}
