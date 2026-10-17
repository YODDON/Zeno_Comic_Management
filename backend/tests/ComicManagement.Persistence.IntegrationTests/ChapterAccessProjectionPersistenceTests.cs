using System.Data.Common;
using ComicManagement.Catalog.Chapters;
using ComicManagement.Catalog.Comics;
using ComicManagement.Billing.Payments;
using ComicManagement.Persistence;
using ComicManagement.Reading.Access;
using ComicManagement.Reading.Unlocks;
using ComicManagement.Shared.Time;
using ComicManagement.Identity.Users;
using ComicManagement.Membership.Plans;
using ComicManagement.Membership.Subscriptions;
using ComicManagement.Wallet.Lots;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;
using WalletAccount = ComicManagement.Wallet.Accounts.Wallet;

namespace ComicManagement.Persistence.IntegrationTests;

public sealed class ChapterAccessProjectionPersistenceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 13, 11, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:18.0-alpine3.22")
            .WithDatabase("comic_management_reading_access_tests")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

    private ServiceProvider _services = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _services = new ServiceCollection()
            .AddPersistence(_postgres.GetConnectionString())
            .BuildServiceProvider(validateScopes: true);
        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Batch_projection_returns_only_public_valid_chapters_with_inherited_rules()
    {
        var comicId = Guid.NewGuid();
        var freeChapterId = Guid.NewGuid();
        var inheritedChapterId = Guid.NewGuid();
        var hiddenChapterId = Guid.NewGuid();
        await using (var scope = _services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var comic = Comic.Create(
                comicId,
                $"r01-access-{comicId:N}",
                "R01 Access",
                string.Empty,
                ComicSeriesStatuses.Ongoing,
                null,
                25,
                true,
                Now);
            AddPublishedChapter(context, comicId, freeChapterId, 1, 0, false);
            AddPublishedChapter(context, comicId, inheritedChapterId, 2, null, null);
            var hidden = AddPublishedChapter(
                context,
                comicId,
                hiddenChapterId,
                3,
                40,
                true);
            hidden.Hide(canHide: true, expectedVersion: 2, Now.AddMinutes(3));
            comic.Publish(true, 1, Now.AddMinutes(4));
            context.Comics.Add(comic);
            await context.SaveChangesAsync();
        }

        await using var queryScope = _services.CreateAsyncScope();
        var store = queryScope.ServiceProvider
            .GetRequiredService<IChapterAccessProjectionStore>();

        var projections = await store.GetManyAsync(
            [freeChapterId, inheritedChapterId, hiddenChapterId],
            Guid.NewGuid());

        Assert.Equal(2, projections.Count);
        Assert.Equal(0, projections[freeChapterId].EffectivePriceCoins);
        Assert.False(projections[freeChapterId].VipEligible);
        Assert.Equal(25, projections[inheritedChapterId].EffectivePriceCoins);
        Assert.True(projections[inheritedChapterId].VipEligible);
        Assert.False(projections[inheritedChapterId].HasActiveVip);
        Assert.False(projections[inheritedChapterId].HasCoinUnlock);
        Assert.DoesNotContain(hiddenChapterId, projections.Keys);
    }

    [Fact]
    public async Task Manifest_source_rechecks_publication_and_returns_only_ready_pages_in_order()
    {
        var comicId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        await using (var scope = _services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var comic = Comic.Create(
                comicId,
                $"r01-manifest-{comicId:N}",
                "R01 Manifest",
                string.Empty,
                ComicSeriesStatuses.Ongoing,
                null,
                0,
                false,
                Now);
            var chapter = Chapter.Create(
                chapterId,
                comicId,
                "Chapter 1",
                1,
                null,
                null,
                Now);
            AddReadyPage(context, chapterId, 2, "r01/page-2");
            AddReadyPage(context, chapterId, 1, "r01/page-1");
            context.ChapterPages.Add(ChapterPage.CreatePending(
                Guid.NewGuid(),
                chapterId,
                3,
                "r01/pending-page",
                $"sha256:{3:x64}",
                Now));
            chapter.Publish(true, 1, Now.AddMinutes(1));
            comic.Publish(true, 1, Now.AddMinutes(2));
            context.Chapters.Add(chapter);
            context.Comics.Add(comic);
            await context.SaveChangesAsync();
        }

        await using (var queryScope = _services.CreateAsyncScope())
        {
            var store = queryScope.ServiceProvider
                .GetRequiredService<IChapterAccessProjectionStore>();
            var source = await store.GetManifestSourceAsync(chapterId, null);

            Assert.NotNull(source);
            Assert.Equal([1, 2], source.Pages.Select(page => page.Order));
            Assert.Equal(["r01/page-1", "r01/page-2"],
                source.Pages.Select(page => page.AssetKey));
        }

        await using (var hideScope = _services.CreateAsyncScope())
        {
            var context = hideScope.ServiceProvider.GetRequiredService<AppDbContext>();
            await context.Chapters
                .Where(chapter => chapter.Id == chapterId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(
                        chapter => chapter.PublicationStatus,
                        ChapterPublicationStatuses.Hidden));
        }

        await using var finalScope = _services.CreateAsyncScope();
        var finalStore = finalScope.ServiceProvider
            .GetRequiredService<IChapterAccessProjectionStore>();
        Assert.Null(await finalStore.GetManifestSourceAsync(chapterId, null));
    }

    [Fact]
    public async Task Manifest_access_and_ready_pages_are_loaded_in_one_database_statement()
    {
        var comicId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        await using (var seedScope = _services.CreateAsyncScope())
        {
            var context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var comic = Comic.Create(
                comicId,
                $"r01-atomic-manifest-{comicId:N}",
                "R01 Atomic Manifest",
                string.Empty,
                ComicSeriesStatuses.Ongoing,
                null,
                0,
                false,
                Now);
            AddPublishedChapter(context, comicId, chapterId, 1, 0, false);
            AddReadyPage(context, chapterId, 2, "r01/atomic-page-2");
            comic.Publish(true, 1, Now.AddMinutes(2));
            context.Comics.Add(comic);
            await context.SaveChangesAsync();
        }

        var interceptor = new ManifestCommandInterceptor();
        var options = new DbContextOptionsBuilder<AppDbContext>();
        PersistenceServiceCollectionExtensions.ConfigurePostgreSql(
            options,
            _postgres.GetConnectionString());
        options.AddInterceptors(interceptor);
        await using var queryContext = new AppDbContext(options.Options);
        var store = new ChapterAccessProjectionStore(queryContext);

        var source = await store.GetManifestSourceAsync(chapterId, null);

        Assert.NotNull(source);
        Assert.Equal(1, interceptor.ManifestReaderCommandCount);
        Assert.Contains("JOIN", interceptor.ManifestCommandText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("chapter_pages", interceptor.ManifestCommandText, StringComparison.Ordinal);
        Assert.Contains("publication_status", interceptor.ManifestCommandText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Vip_entitlement_is_loaded_with_access_and_expires_authoritatively()
    {
        var userId = Guid.NewGuid();
        var comicId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        await using (var seedScope = _services.CreateAsyncScope())
        {
            var context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTimeOffset.UtcNow;
            context.Users.Add(User.CreateFromGoogle(
                userId,
                "V01 Reader",
                null,
                null,
                now));
            var comic = Comic.Create(
                comicId,
                $"v01-access-{comicId:N}",
                "V01 Access",
                string.Empty,
                ComicSeriesStatuses.Ongoing,
                null,
                100,
                true,
                now);
            var chapter = Chapter.Create(
                chapterId,
                comicId,
                "VIP Chapter",
                1,
                null,
                null,
                now);
            AddReadyPage(context, chapterId, 1, $"v01/{chapterId:N}/1");
            chapter.Publish(true, 1, now.AddSeconds(1));
            comic.Publish(true, 1, now.AddSeconds(2));
            context.Comics.Add(comic);
            context.Chapters.Add(chapter);
            var (subscription, _) = VipSubscription.Create(
                userId,
                now,
                VipPlan.FixedDurationHours);
            context.VipSubscriptions.Add(subscription);
            await context.SaveChangesAsync();
        }

        await using (var activeScope = _services.CreateAsyncScope())
        {
            var handler = new ChapterAccessHandler(
                activeScope.ServiceProvider.GetRequiredService<IChapterAccessProjectionStore>(),
                new UnusedContentProvider(),
                new StubClock(DateTimeOffset.UtcNow));
            var active = await handler.GetAccessAsync(chapterId, userId);
            Assert.Equal(ChapterAccessTypes.VipIncluded, active.Access!.AccessType);
            Assert.True(active.Access.CanRead);
            Assert.Equal(
                ChapterManifestOutcome.ProtectedDeliveryUnavailable,
                (await handler.GetManifestAsync(chapterId, userId)).Outcome);
        }

        await using (var expireScope = _services.CreateAsyncScope())
        {
            var context = expireScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var expiredAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            await context.VipSubscriptions
                .Where(subscription => subscription.UserId == userId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(
                        subscription => subscription.ExpiresAt,
                        expiredAt)
                    .SetProperty(
                        subscription => subscription.LastFulfilledAt,
                        expiredAt.AddHours(-720)));
        }

        await using var expiredScope = _services.CreateAsyncScope();
        var expiredHandler = new ChapterAccessHandler(
            expiredScope.ServiceProvider.GetRequiredService<IChapterAccessProjectionStore>(),
            new UnusedContentProvider(),
            new StubClock(DateTimeOffset.UtcNow));
        var expired = await expiredHandler.GetAccessAsync(chapterId, userId);
        Assert.Equal(ChapterAccessTypes.Locked, expired.Access!.AccessType);
        Assert.False(expired.Access.CanRead);
    }

    [Fact]
    public async Task Unlock_quote_persists_five_minute_vip_price_and_wallet_estimate()
    {
        var userId = Guid.NewGuid();
        var comicId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using (var seedScope = _services.CreateAsyncScope())
        {
            var context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            context.Users.Add(User.CreateFromGoogle(
                userId,
                "U01 Quote Reader",
                null,
                null,
                now));
            var comic = Comic.Create(
                comicId,
                $"u01-quote-{comicId:N}",
                "U01 Quote",
                string.Empty,
                ComicSeriesStatuses.Ongoing,
                null,
                101,
                false,
                now);
            AddPublishedChapter(context, comicId, chapterId, 1, null, null);
            comic.Publish(true, 1, now.AddSeconds(2));
            context.Comics.Add(comic);
            var wallet = WalletAccount.Create(userId, now);
            wallet.CreditBonus(30, now.AddSeconds(1));
            wallet.CreditPaid(1_000, now.AddSeconds(2));
            context.Wallets.Add(wallet);
            var topUpOrderId = Guid.NewGuid();
            var topUpOrder = PaymentOrder.CreateTopUp(
                topUpOrderId,
                userId,
                $"TOPUP-{topUpOrderId:N}",
                10_000,
                1_000,
                $"u01-quote-{topUpOrderId:N}",
                new string('a', PaymentOrder.RequestFingerprintLength),
                now,
                now.AddMinutes(30));
            topUpOrder.FulfillTopUp(now.AddSeconds(2));
            context.PaymentOrders.Add(topUpOrder);
            context.PaidCoinLots.Add(PaidCoinLot.Create(
                Guid.NewGuid(),
                userId,
                topUpOrderId,
                1_000,
                now.AddSeconds(2)));
            var (subscription, _) = VipSubscription.Create(
                userId,
                now,
                VipPlan.FixedDurationHours);
            context.VipSubscriptions.Add(subscription);
            await context.SaveChangesAsync();
        }

        ChapterUnlockQuoteState quote;
        await using (var quoteScope = _services.CreateAsyncScope())
        {
            var result = await quoteScope.ServiceProvider
                .GetRequiredService<ChapterUnlockQuoteHandler>()
                .CreateAsync(userId, chapterId);
            Assert.Equal(ChapterUnlockQuoteOutcome.Succeeded, result.Outcome);
            quote = Assert.IsType<ChapterUnlockQuoteState>(result.Quote);
        }

        Assert.Equal(101, quote.BasePriceCoins);
        Assert.True(quote.VipDiscountApplied);
        Assert.Equal(81, quote.FinalPriceCoins);
        Assert.Equal(30, quote.BonusToSpend);
        Assert.Equal(51, quote.PaidToSpend);

        await using var verificationScope = _services.CreateAsyncScope();
        var verification = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var persisted = await verification.ChapterUnlockQuotes
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == quote.QuoteId);
        Assert.Equal(persisted.CreatedAt.AddMinutes(5), persisted.ExpiresAt);
        Assert.Equal(quote.PriceRevision, persisted.PriceRevision);
        var walletAfter = await verification.Wallets.AsNoTracking().SingleAsync(candidate =>
            candidate.UserId == userId);
        Assert.Equal(30, walletAfter.BonusAvailable);
        Assert.Equal(1_000, walletAfter.PaidAvailable);

        var invalid = await Assert.ThrowsAsync<PostgresException>(() =>
            verification.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO reading.chapter_unlock_quotes
                    (id, user_id, chapter_id, base_price_coins, vip_discount_applied,
                     vip_discount_percent, final_price_coins, bonus_to_spend, paid_to_spend,
                     price_revision, expires_at, created_at)
                VALUES
                    ({Guid.NewGuid()}, {userId}, {chapterId}, {101L}, {true}, {20}, {80L},
                     {30L}, {50L}, {new string('a', 64)},
                     {persisted.CreatedAt.AddMinutes(5)}, {persisted.CreatedAt});
                """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, invalid.SqlState);
        Assert.Equal("ck_chapter_unlock_quotes_price", invalid.ConstraintName);

        var mutation = await Assert.ThrowsAsync<PostgresException>(() =>
            verification.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE reading.chapter_unlock_quotes
                SET price_revision = {new string('b', 64)}
                WHERE id = {quote.QuoteId};
                """));
        Assert.Equal("23000", mutation.SqlState);
        Assert.Contains("append-only", mutation.MessageText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Hidden_or_free_chapter_does_not_persist_unlock_quote()
    {
        var userId = Guid.NewGuid();
        var comicId = Guid.NewGuid();
        var freeChapterId = Guid.NewGuid();
        var hiddenChapterId = Guid.NewGuid();
        await using (var seedScope = _services.CreateAsyncScope())
        {
            var context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            context.Users.Add(User.CreateFromGoogle(
                userId,
                "U01 Quote Guard",
                null,
                null,
                Now));
            var comic = Comic.Create(
                comicId,
                $"u01-guard-{comicId:N}",
                "U01 Guard",
                string.Empty,
                ComicSeriesStatuses.Ongoing,
                null,
                100,
                false,
                Now);
            AddPublishedChapter(context, comicId, freeChapterId, 1, 0, false);
            var hidden = AddPublishedChapter(context, comicId, hiddenChapterId, 2, 100, false);
            hidden.Hide(canHide: true, expectedVersion: 2, Now.AddMinutes(3));
            comic.Publish(true, 1, Now.AddMinutes(4));
            context.Comics.Add(comic);
            await context.SaveChangesAsync();
        }

        await using var quoteScope = _services.CreateAsyncScope();
        var handler = quoteScope.ServiceProvider.GetRequiredService<ChapterUnlockQuoteHandler>();
        Assert.Equal(
            ChapterUnlockQuoteOutcome.UnlockNotRequired,
            (await handler.CreateAsync(userId, freeChapterId)).Outcome);
        Assert.Equal(
            ChapterUnlockQuoteOutcome.NotFound,
            (await handler.CreateAsync(userId, hiddenChapterId)).Outcome);
        Assert.Empty(await quoteScope.ServiceProvider.GetRequiredService<AppDbContext>()
            .ChapterUnlockQuotes.ToArrayAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Page_delivery_rechecks_postgresql_after_concurrent_hide_or_reprice(
        bool hideChapter)
    {
        var comicId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        await using (var seedScope = _services.CreateAsyncScope())
        {
            var context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var comic = Comic.Create(
                comicId,
                $"r01-content-race-{comicId:N}",
                "R01 Content Race",
                string.Empty,
                ComicSeriesStatuses.Ongoing,
                null,
                0,
                false,
                Now);
            AddPublishedChapter(context, comicId, chapterId, 1, 0, false);
            comic.Publish(true, 1, Now.AddMinutes(2));
            context.Comics.Add(comic);
            await context.SaveChangesAsync();
        }

        await using var readScope = _services.CreateAsyncScope();
        var provider = new BlockingContentProvider();
        var handler = new ChapterAccessHandler(
            readScope.ServiceProvider.GetRequiredService<IChapterAccessProjectionStore>(),
            provider,
            new StubClock(Now));
        var delivery = handler.GetPageContentAsync(chapterId, 1, null);
        await provider.Entered;

        await using (var mutationScope = _services.CreateAsyncScope())
        {
            var context = mutationScope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (hideChapter)
            {
                await context.Chapters
                    .Where(chapter => chapter.Id == chapterId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(
                        chapter => chapter.PublicationStatus,
                        ChapterPublicationStatuses.Hidden));
            }
            else
            {
                await context.Chapters
                    .Where(chapter => chapter.Id == chapterId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(
                        chapter => chapter.PriceOverrideCoins,
                        (long?)25));
            }
        }

        provider.Release();
        var result = await delivery;

        Assert.Equal(
            hideChapter
                ? ChapterManifestOutcome.NotFound
                : ChapterManifestOutcome.AuthenticationRequired,
            result.Outcome);
        Assert.Null(result.Content);
    }

    private static Chapter AddPublishedChapter(
        AppDbContext context,
        Guid comicId,
        Guid chapterId,
        int sortOrder,
        long? priceOverride,
        bool? vipOverride)
    {
        var chapter = Chapter.Create(
            chapterId,
            comicId,
            $"Chapter {sortOrder}",
            sortOrder,
            priceOverride,
            vipOverride,
            Now);
        AddReadyPage(context, chapterId, 1, $"r01/{chapterId:N}/1");
        chapter.Publish(true, 1, Now.AddMinutes(sortOrder));
        context.Chapters.Add(chapter);
        return chapter;
    }

    private static void AddReadyPage(
        AppDbContext context,
        Guid chapterId,
        int pageOrder,
        string publicId)
    {
        var page = ChapterPage.CreatePending(
            Guid.NewGuid(),
            chapterId,
            pageOrder,
            publicId,
            $"sha256:{pageOrder:x64}",
            Now);
        page.MarkReady(1200, 1800);
        context.ChapterPages.Add(page);
    }

    private sealed class BlockingContentProvider : IChapterPageContentProvider
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Entered => _entered.Task;

        internal void Release() => _release.TrySetResult();

        public async Task<ChapterPageContent?> GetAsync(
            string assetKey,
            CancellationToken cancellationToken = default)
        {
            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return new ChapterPageContent([1, 2, 3], "image/webp");
        }
    }

    private sealed class UnusedContentProvider : IChapterPageContentProvider
    {
        public Task<ChapterPageContent?> GetAsync(
            string assetKey,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "Protected VIP access must fail closed before provider delivery.");
    }

    private sealed class StubClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow => utcNow;
    }

    private sealed class ManifestCommandInterceptor : DbCommandInterceptor
    {
        internal int ManifestReaderCommandCount { get; private set; }

        internal string ManifestCommandText { get; private set; } = string.Empty;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("catalog.chapter_pages", StringComparison.Ordinal))
            {
                ManifestReaderCommandCount++;
                ManifestCommandText = command.CommandText;
            }

            return ValueTask.FromResult(result);
        }
    }
}
