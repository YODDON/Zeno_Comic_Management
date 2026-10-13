namespace ComicManagement.Reading.Access;

public static class ChapterAccessTypes
{
    public const string Free = "Free";
    public const string VipIncluded = "VipIncluded";
    public const string CoinUnlocked = "CoinUnlocked";
    public const string Locked = "Locked";
    public const string Unavailable = "Unavailable";
}

public enum ChapterAccessOutcome
{
    Succeeded = 0,
    NotFound = 1,
}

public enum ChapterManifestOutcome
{
    Succeeded = 0,
    NotFound = 1,
    AuthenticationRequired = 2,
    Forbidden = 3,
    ProtectedDeliveryUnavailable = 4,
    ProviderUnavailable = 5,
}

public sealed record ChapterAccessProjection(
    Guid ChapterId,
    long EffectivePriceCoins,
    bool VipEligible,
    bool HasActiveVip,
    bool HasCoinUnlock);

public sealed record ChapterManifestPageSource(
    int Order,
    int Width,
    int Height,
    string AssetKey);

public sealed record ChapterManifestSource(
    ChapterAccessProjection Access,
    IReadOnlyList<ChapterManifestPageSource> Pages);

public sealed record ChapterAccessDecision(
    Guid ChapterId,
    string AccessType,
    bool CanRead,
    long PriceCoins,
    bool NeedsLogin);

public sealed record ChapterAccessResult(
    ChapterAccessOutcome Outcome,
    ChapterAccessDecision? Access = null);

public sealed record ChapterManifestPage(
    int Order,
    int Width,
    int Height,
    string DeliveryUrl);

public sealed record ChapterManifest(
    Guid ChapterId,
    IReadOnlyList<ChapterManifestPage> Pages,
    DateTimeOffset ManifestIssuedAt,
    string RefreshHint);

public sealed record ChapterManifestResult(
    ChapterManifestOutcome Outcome,
    ChapterManifest? Manifest = null);

public interface IChapterAccessProjectionStore
{
    Task<IReadOnlyDictionary<Guid, ChapterAccessProjection>> GetManyAsync(
        IReadOnlyCollection<Guid> chapterIds,
        Guid? userId,
        CancellationToken cancellationToken = default);

    Task<ChapterManifestSource?> GetManifestSourceAsync(
        Guid chapterId,
        Guid? userId,
        CancellationToken cancellationToken = default);
}

public interface IChapterPageDeliveryUrlProvider
{
    bool TryCreateFreeDeliveryUrl(string assetKey, out string deliveryUrl);
}

public sealed class ChapterAccessHandler(
    IChapterAccessProjectionStore store,
    IChapterPageDeliveryUrlProvider deliveryUrlProvider)
{
    public const string FreeManifestRefreshHint = "Refresh after an image delivery error.";

    public async Task<ChapterAccessResult> GetAccessAsync(
        Guid chapterId,
        Guid? userId,
        CancellationToken cancellationToken = default)
    {
        if (chapterId == Guid.Empty)
        {
            return new ChapterAccessResult(ChapterAccessOutcome.NotFound);
        }

        var projections = await store.GetManyAsync([chapterId], userId, cancellationToken);
        return projections.TryGetValue(chapterId, out var projection)
            ? new ChapterAccessResult(
                ChapterAccessOutcome.Succeeded,
                Resolve(projection, userId.HasValue))
            : new ChapterAccessResult(ChapterAccessOutcome.NotFound);
    }

    public async Task<IReadOnlyDictionary<Guid, ChapterAccessDecision>> GetAccessBatchAsync(
        IReadOnlyCollection<Guid> chapterIds,
        Guid? userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chapterIds);
        var distinctIds = chapterIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray();
        if (distinctIds.Length == 0)
        {
            return new Dictionary<Guid, ChapterAccessDecision>();
        }

        var projections = await store.GetManyAsync(distinctIds, userId, cancellationToken);
        return projections.ToDictionary(
            pair => pair.Key,
            pair => Resolve(pair.Value, userId.HasValue));
    }

    public async Task<ChapterManifestResult> GetManifestAsync(
        Guid chapterId,
        Guid? userId,
        DateTimeOffset issuedAt,
        CancellationToken cancellationToken = default)
    {
        if (chapterId == Guid.Empty || issuedAt.Offset != TimeSpan.Zero)
        {
            return new ChapterManifestResult(ChapterManifestOutcome.NotFound);
        }

        var source = await store.GetManifestSourceAsync(chapterId, userId, cancellationToken);
        if (source is null)
        {
            return new ChapterManifestResult(ChapterManifestOutcome.NotFound);
        }

        var access = Resolve(source.Access, userId.HasValue);
        if (!access.CanRead)
        {
            return new ChapterManifestResult(
                userId.HasValue
                    ? ChapterManifestOutcome.Forbidden
                    : ChapterManifestOutcome.AuthenticationRequired);
        }

        // Protected Cloudinary delivery remains an explicit Q02 verification gate.
        // Never downgrade an entitled paid/VIP request to a shareable Free URL.
        if (access.AccessType is ChapterAccessTypes.VipIncluded or ChapterAccessTypes.CoinUnlocked)
        {
            return new ChapterManifestResult(
                ChapterManifestOutcome.ProtectedDeliveryUnavailable);
        }

        var pages = new List<ChapterManifestPage>(source.Pages.Count);
        foreach (var page in source.Pages)
        {
            if (!deliveryUrlProvider.TryCreateFreeDeliveryUrl(page.AssetKey, out var deliveryUrl))
            {
                return new ChapterManifestResult(ChapterManifestOutcome.ProviderUnavailable);
            }

            pages.Add(new ChapterManifestPage(
                page.Order,
                page.Width,
                page.Height,
                deliveryUrl));
        }

        return new ChapterManifestResult(
            ChapterManifestOutcome.Succeeded,
            new ChapterManifest(
                chapterId,
                pages,
                issuedAt,
                FreeManifestRefreshHint));
    }

    private static ChapterAccessDecision Resolve(
        ChapterAccessProjection projection,
        bool isAuthenticated)
    {
        if (projection.EffectivePriceCoins == 0)
        {
            return new ChapterAccessDecision(
                projection.ChapterId,
                ChapterAccessTypes.Free,
                true,
                0,
                false);
        }

        if (isAuthenticated && projection.HasActiveVip && projection.VipEligible)
        {
            return new ChapterAccessDecision(
                projection.ChapterId,
                ChapterAccessTypes.VipIncluded,
                true,
                projection.EffectivePriceCoins,
                false);
        }

        if (isAuthenticated && projection.HasCoinUnlock)
        {
            return new ChapterAccessDecision(
                projection.ChapterId,
                ChapterAccessTypes.CoinUnlocked,
                true,
                projection.EffectivePriceCoins,
                false);
        }

        return new ChapterAccessDecision(
            projection.ChapterId,
            ChapterAccessTypes.Locked,
            false,
            projection.EffectivePriceCoins,
            !isAuthenticated);
    }
}
