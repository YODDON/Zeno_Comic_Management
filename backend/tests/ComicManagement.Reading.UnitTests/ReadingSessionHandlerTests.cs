using ComicManagement.Reading.Sessions;
using ComicManagement.Shared.Time;
using Xunit;

namespace ComicManagement.Reading.UnitTests;

public sealed class ReadingSessionHandlerTests
{
    private static readonly DateTimeOffset StartedAt =
        new(2026, 10, 14, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Handler_canonicalizes_heartbeat_fingerprint()
    {
        var store = new CapturingStore();
        var handler = new ReadingSessionHandler(store, new StubClock(StartedAt));

        await handler.UpdateProgressAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            0.95m,
            1);

        Assert.Matches("^[0-9a-f]{64}$", store.Fingerprint);
    }

    [Fact]
    public void Session_has_absolute_non_sliding_thirty_minute_expiry()
    {
        var session = CreateSession();

        session.ApplyHeartbeat(10, 0.5m, 1, Fingerprint('a'), "revision-2", [10],
            StartedAt.AddMinutes(20));

        Assert.Equal(StartedAt.AddMinutes(30), session.ExpiresAt);
        Assert.True(session.IsExpiredAt(StartedAt.AddMinutes(30)));
    }

    [Fact]
    public void First_heartbeat_credits_zero_and_later_heartbeat_is_capped_at_fifteen_seconds()
    {
        var session = CreateSession();

        var first = session.ApplyHeartbeat(
            10, 0.25m, 1, Fingerprint('a'), "revision-1", [10, 20], StartedAt.AddSeconds(1));
        var second = session.ApplyHeartbeat(
            20, 0.5m, 2, Fingerprint('b'), "revision-1", [10, 20], StartedAt.AddSeconds(31));

        Assert.Equal(ReadingHeartbeatOutcome.Accepted, first.Outcome);
        Assert.Equal(ReadingHeartbeatOutcome.Accepted, second.Outcome);
        Assert.Equal(15_000, session.ActiveDurationMs);
    }

    [Fact]
    public void Same_sequence_and_fingerprint_is_side_effect_free_replay()
    {
        var session = CreateSession();
        session.ApplyHeartbeat(
            10, 0.5m, 1, Fingerprint('a'), "revision-1", [10, 20], StartedAt.AddSeconds(1));
        var version = session.Version;

        var replay = session.ApplyHeartbeat(
            10, 0.5m, 1, Fingerprint('a'), "revision-2", [10, 20], StartedAt.AddSeconds(10));

        Assert.Equal(ReadingHeartbeatOutcome.Replayed, replay.Outcome);
        Assert.Equal(0, session.ActiveDurationMs);
        Assert.Equal(version, session.Version);
        Assert.Equal("revision-1", session.ManifestRevision);
    }

    [Theory]
    [InlineData(2, 'c', ReadingSequenceConflictReasons.PayloadMismatch)]
    [InlineData(1, 'a', ReadingSequenceConflictReasons.Stale)]
    [InlineData(4, 'd', ReadingSequenceConflictReasons.Gap)]
    public void Invalid_sequence_is_rejected(
        long sequence,
        char fingerprintCharacter,
        string expectedReason)
    {
        var session = CreateSession();
        session.ApplyHeartbeat(
            10, 0.5m, 1, Fingerprint('a'), "revision-1", [10, 20], StartedAt.AddSeconds(1));
        session.ApplyHeartbeat(
            20, 0.5m, 2, Fingerprint('b'), "revision-1", [10, 20], StartedAt.AddSeconds(2));

        var result = session.ClassifySequence(sequence, Fingerprint(fingerprintCharacter));

        Assert.Equal(ReadingHeartbeatOutcome.SequenceConflict, result.Outcome);
        Assert.Equal(expectedReason, result.ConflictReason);
    }

    [Fact]
    public void Progress_uses_ready_page_ordinal_and_never_decreases_across_manifest_changes()
    {
        var session = CreateSession(pageCount: 3);
        session.ApplyHeartbeat(
            30, 0.5m, 1, Fingerprint('a'), "revision-1", [10, 20, 30], StartedAt.AddSeconds(1));
        Assert.Equal(8_333, session.MaxProgressBasisPoints);

        session.ApplyHeartbeat(
            20, 0m, 2, Fingerprint('b'), "revision-2", [10, 20], StartedAt.AddSeconds(2));

        Assert.Equal(8_333, session.MaxProgressBasisPoints);
        Assert.Equal("revision-2", session.ManifestRevision);
        Assert.Equal(2, session.ManifestPageCount);
    }

    [Fact]
    public void Page_that_is_no_longer_ready_cannot_advance_progress_or_active_time()
    {
        var session = CreateSession(pageCount: 2);
        session.ApplyHeartbeat(
            10, 0.5m, 1, Fingerprint('a'), "revision-1", [10, 20], StartedAt.AddSeconds(1));

        session.ApplyHeartbeat(
            20, 1m, 2, Fingerprint('b'), "revision-2", [10], StartedAt.AddSeconds(10));

        Assert.Equal(2_500, session.MaxProgressBasisPoints);
        Assert.Equal(0, session.ActiveDurationMs);
        Assert.Equal(10, session.CurrentPageOrder);

        session.ApplyHeartbeat(
            10, 1m, 3, Fingerprint('c'), "revision-2", [10], StartedAt.AddSeconds(20));

        Assert.Equal(10_000, session.MaxProgressBasisPoints);
        Assert.Equal(0, session.ActiveDurationMs);
    }

    [Fact]
    public void Qualification_requires_fifteen_seconds_and_is_one_time_per_session()
    {
        var session = CreateSession();
        session.ApplyHeartbeat(
            10, 0.95m, 1, Fingerprint('a'), "revision-1", [10], StartedAt.AddSeconds(1));
        Assert.Equal(
            ReadingQualificationOutcome.ActiveDurationThresholdNotMet,
            session.Qualify(StartedAt.AddSeconds(2)));

        session.ApplyHeartbeat(
            10, 0.95m, 2, Fingerprint('b'), "revision-1", [10], StartedAt.AddSeconds(16));
        Assert.Equal(
            ReadingQualificationOutcome.Qualified,
            session.Qualify(StartedAt.AddSeconds(17)));
        var version = session.Version;

        Assert.Equal(
            ReadingQualificationOutcome.AlreadyQualified,
            session.Qualify(StartedAt.AddSeconds(18)));
        Assert.Equal(version, session.Version);
    }

    [Fact]
    public void Older_session_cannot_overwrite_newer_cross_tab_progress()
    {
        var userId = Guid.NewGuid();
        var comicId = Guid.NewGuid();
        var newerSessionId = Guid.NewGuid();
        var progress = ReadingProgress.Create(
            userId,
            comicId,
            Guid.NewGuid(),
            3,
            0.5m,
            StartedAt.AddMinutes(2),
            newerSessionId,
            StartedAt.AddMinutes(1));

        var applied = progress.Apply(
            Guid.NewGuid(),
            1,
            0.1m,
            StartedAt.AddMinutes(3),
            Guid.NewGuid(),
            StartedAt);

        Assert.False(applied);
        Assert.Equal(newerSessionId, progress.SourceSessionId);
        Assert.Equal(3, progress.LastPageOrder);
    }

    private static ReadingSession CreateSession(int pageCount = 1) => ReadingSession.Create(
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        "revision-1",
        pageCount,
        ReadingQualificationPolicy.R02V1,
        StartedAt);

    private static string Fingerprint(char character) => new(character, 64);

    private sealed class CapturingStore : IReadingSessionStore
    {
        internal string Fingerprint { get; private set; } = string.Empty;

        public Task<ReadingSessionCommandResult> CreateAsync(
            Guid sessionId,
            Guid userId,
            Guid chapterId,
            ReadingQualificationPolicy policy,
            DateTimeOffset startedAt,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ReadingSessionCommandResult> UpdateProgressAsync(
            Guid sessionId,
            Guid userId,
            int currentPageOrder,
            decimal currentPageOffsetRatio,
            long sequenceNumber,
            string requestFingerprint,
            DateTimeOffset receivedAt,
            CancellationToken cancellationToken = default)
        {
            Fingerprint = requestFingerprint;
            return Task.FromResult(new ReadingSessionCommandResult(
                ReadingSessionCommandOutcome.NotFound));
        }

        public Task<ReadingSessionCommandResult> QualifyAsync(
            Guid sessionId,
            Guid userId,
            DateTimeOffset qualifiedAt,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class StubClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow => utcNow;
    }
}
