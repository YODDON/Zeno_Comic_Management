using System.Buffers.Binary;
using ComicManagement.Shared.Time;

namespace ComicManagement.Missions.Queries;

public sealed record MissionState(
    Guid MissionId,
    Guid RevisionId,
    string Code,
    string Name,
    string Description,
    string ActionType,
    string CycleType,
    string PeriodKey,
    int TargetCount,
    int ProgressCount,
    long RewardBonusCoins,
    string ProgressStatus,
    string RewardStatus,
    DateTimeOffset EffectiveFrom,
    DateTimeOffset? EffectiveUntil);

public sealed record MissionDailyLimitState(
    DateOnly LocalDate,
    long LimitCoins,
    long GrantedCoins,
    long RemainingCoins);

public sealed record MissionRewardState(
    Guid RewardGrantId,
    Guid MissionId,
    Guid RevisionId,
    string MissionName,
    string Outcome,
    long RewardBonusCoins,
    long GrantedBonusCoins,
    DateOnly LocalDate,
    DateTimeOffset DecidedAt);

public sealed record MissionRewardCursor(DateTimeOffset DecidedAt, Guid RewardGrantId);

public sealed record MissionRewardPage(
    IReadOnlyList<MissionRewardState> Items,
    string? NextCursor);

public enum MissionQueryOutcome
{
    Succeeded = 0,
    ValidationFailed = 1,
}

public sealed record MissionListResult(
    MissionQueryOutcome Outcome,
    IReadOnlyList<MissionState>? Missions = null);

public sealed record MissionDailyLimitResult(
    MissionQueryOutcome Outcome,
    MissionDailyLimitState? DailyLimit = null);

public sealed record MissionRewardListResult(
    MissionQueryOutcome Outcome,
    MissionRewardPage? Page = null);

public interface IMissionQueryStore
{
    Task<IReadOnlyList<MissionState>> ListCurrentAsync(
        Guid userId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<MissionDailyLimitState> GetDailyLimitAsync(
        Guid userId,
        DateOnly localDate,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MissionRewardState>> ListRewardsAsync(
        Guid userId,
        MissionRewardCursor? cursor,
        int take,
        CancellationToken cancellationToken = default);
}

public sealed class MissionQueryHandler(
    IMissionQueryStore store,
    IClock clock)
{
    public const int DefaultPageSize = 30;
    public const int MaximumPageSize = 100;
    public const int CursorLength = 32;

    public async Task<MissionListResult> ListAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty)
        {
            return new MissionListResult(MissionQueryOutcome.ValidationFailed);
        }

        var missions = await store.ListCurrentAsync(
            userId,
            PostgreSqlTimestamp.Normalize(clock.UtcNow),
            cancellationToken);
        return new MissionListResult(MissionQueryOutcome.Succeeded, missions);
    }

    public async Task<MissionDailyLimitResult> GetDailyLimitAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty)
        {
            return new MissionDailyLimitResult(MissionQueryOutcome.ValidationFailed);
        }

        var localDate = Periods.MissionPeriodResolver.GetVietnamLocalDate(
            PostgreSqlTimestamp.Normalize(clock.UtcNow));
        return new MissionDailyLimitResult(
            MissionQueryOutcome.Succeeded,
            await store.GetDailyLimitAsync(userId, localDate, cancellationToken));
    }

    public async Task<MissionRewardListResult> ListRewardsAsync(
        Guid userId,
        string? cursor,
        int? limit,
        CancellationToken cancellationToken = default)
    {
        var pageSize = limit ?? DefaultPageSize;
        if (userId == Guid.Empty || pageSize is < 1 or > MaximumPageSize ||
            !TryDecodeCursor(cursor, out var decodedCursor))
        {
            return new MissionRewardListResult(MissionQueryOutcome.ValidationFailed);
        }

        var rewards = await store.ListRewardsAsync(
            userId,
            decodedCursor,
            pageSize + 1,
            cancellationToken);
        var items = rewards.Take(pageSize).ToArray();
        var nextCursor = rewards.Count > pageSize
            ? EncodeCursor(items[^1])
            : null;
        return new MissionRewardListResult(
            MissionQueryOutcome.Succeeded,
            new MissionRewardPage(items, nextCursor));
    }

    private static string EncodeCursor(MissionRewardState reward)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long) + 16];
        BinaryPrimitives.WriteInt64BigEndian(bytes, reward.DecidedAt.UtcTicks);
        reward.RewardGrantId.TryWriteBytes(bytes[sizeof(long)..]);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static bool TryDecodeCursor(string? cursor, out MissionRewardCursor? value)
    {
        value = null;
        if (string.IsNullOrEmpty(cursor))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(cursor) || cursor.Length != CursorLength)
        {
            return false;
        }

        try
        {
            var normalized = cursor.Replace('-', '+').Replace('_', '/');
            var bytes = Convert.FromBase64String(normalized.PadRight(
                normalized.Length + (4 - normalized.Length % 4) % 4,
                '='));
            if (bytes.Length != sizeof(long) + 16)
            {
                return false;
            }

            var ticks = BinaryPrimitives.ReadInt64BigEndian(bytes);
            var id = new Guid(bytes.AsSpan(sizeof(long), 16));
            if (ticks < DateTimeOffset.MinValue.UtcTicks ||
                ticks > DateTimeOffset.MaxValue.UtcTicks || id == Guid.Empty)
            {
                return false;
            }

            value = new MissionRewardCursor(new DateTimeOffset(ticks, TimeSpan.Zero), id);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
