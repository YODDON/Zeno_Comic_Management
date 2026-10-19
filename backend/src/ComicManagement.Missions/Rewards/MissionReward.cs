namespace ComicManagement.Missions.Rewards;

public static class MissionRewardOutcomes
{
    public const string Granted = "Granted";
    public const string DeniedDailyCap = "DeniedDailyCap";
}

public sealed class MissionRewardGrant
{
    private MissionRewardGrant()
    {
    }

    public Guid Id { get; private set; }
    public Guid ProgressId { get; private set; }
    public Guid UserId { get; private set; }
    public long BonusCoins { get; private set; }
    public string Outcome { get; private set; } = string.Empty;
    public DateOnly LocalDate { get; private set; }
    public DateTimeOffset DecidedAt { get; private set; }
    public Guid? WalletOperationId { get; private set; }

    public static MissionRewardGrant Create(
        Guid id,
        Guid progressId,
        Guid userId,
        long bonusCoins,
        string outcome,
        DateOnly localDate,
        DateTimeOffset decidedAt,
        Guid? walletOperationId)
    {
        if (id == Guid.Empty || progressId == Guid.Empty || userId == Guid.Empty ||
            bonusCoins is < 1 or > MissionDailyCounter.MaximumDailyBonusCoins ||
            outcome is not (MissionRewardOutcomes.Granted or MissionRewardOutcomes.DeniedDailyCap) ||
            decidedAt.Offset != TimeSpan.Zero ||
            (outcome == MissionRewardOutcomes.Granted) != walletOperationId.HasValue ||
            walletOperationId == Guid.Empty)
        {
            throw new ArgumentException("Mission reward grant is invalid.");
        }

        return new MissionRewardGrant
        {
            Id = id,
            ProgressId = progressId,
            UserId = userId,
            BonusCoins = bonusCoins,
            Outcome = outcome,
            LocalDate = localDate,
            DecidedAt = decidedAt,
            WalletOperationId = walletOperationId,
        };
    }
}

public sealed class MissionDailyCounter
{
    public const long MaximumDailyBonusCoins = 300;

    private MissionDailyCounter()
    {
    }

    public Guid UserId { get; private set; }
    public DateOnly LocalDate { get; private set; }
    public long GrantedTotal { get; private set; }
    public long Version { get; private set; }

    public static MissionDailyCounter Create(Guid userId, DateOnly localDate)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("Mission daily counter user is required.", nameof(userId));
        }

        return new MissionDailyCounter
        {
            UserId = userId,
            LocalDate = localDate,
            Version = 1,
        };
    }

    public bool TryGrant(long amount)
    {
        if (amount is < 1 or > MaximumDailyBonusCoins)
        {
            throw new ArgumentOutOfRangeException(nameof(amount));
        }

        if (GrantedTotal > MaximumDailyBonusCoins - amount)
        {
            return false;
        }

        GrantedTotal += amount;
        Version = checked(Version + 1);
        return true;
    }
}

public static class MissionEventTypes
{
    public const string RewardGranted = "Missions.MissionRewardGranted.v1";
}

public sealed record MissionRewardGranted(
    Guid RewardGrantId,
    Guid UserId,
    Guid MissionRevisionId,
    Guid ProgressId,
    long BonusCoins,
    DateOnly ActionLocalDate,
    DateTimeOffset GrantedAt);
