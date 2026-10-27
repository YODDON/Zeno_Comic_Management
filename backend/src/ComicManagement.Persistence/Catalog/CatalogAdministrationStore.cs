using ComicManagement.Administration.Auditing;
using ComicManagement.Catalog.Administration;
using ComicManagement.Catalog;
using ComicManagement.Catalog.Chapters;
using ComicManagement.Catalog.Comics;
using ComicManagement.Catalog.Taxonomy;
using ComicManagement.Shared.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ComicManagement.Persistence;

internal sealed class CatalogAdministrationStore(
    AppDbContext db,
    IConsistentReadExecutor consistentRead,
    ICatalogAdministrationCriticalSectionObserver observer) : ICatalogAdministrationStore
{
    public Task<AdminCatalogPage<AdminComicItem>> ListComicsAsync(
        string? search, string? publicationStatus, int page, int pageSize, CancellationToken token)
        => consistentRead.ExecuteAsync<AdminCatalogPage<AdminComicItem>>(async cancellationToken =>
    {
        var query = db.Comics.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(x => EF.Functions.ILike(x.Title, $"%{term}%") ||
                                     EF.Functions.ILike(x.Slug, $"%{term}%"));
        }
        if (!string.IsNullOrWhiteSpace(publicationStatus))
            query = query.Where(x => x.PublicationStatus == publicationStatus);
        var total = await query.CountAsync(token);
        var comics = await query.OrderByDescending(x => x.UpdatedAt).ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(token);
        await observer.AfterComicRootsReadAsync(token);
        var ids = comics.Select(x => x.Id).ToArray();
        var genres = await db.ComicGenres.AsNoTracking().Where(x => ids.Contains(x.ComicId)).ToListAsync(token);
        var authors = await db.ComicAuthors.AsNoTracking().Where(x => ids.Contains(x.ComicId)).ToListAsync(token);
        var groups = await db.ComicTranslationGroups.AsNoTracking().Where(x => ids.Contains(x.ComicId)).ToListAsync(token);
        return new(comics.Select(x => Map(x, genres, authors, groups)).ToArray(), page, pageSize,
            total, total == 0 ? 0 : (int)Math.Ceiling(total / (double)pageSize));
    }, token);

    public Task<AdminComicItem?> GetComicAsync(Guid id, CancellationToken token) =>
        consistentRead.ExecuteAsync<AdminComicItem?>(async cancellationToken =>
    {
        var comic = await db.Comics.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token);
        if (comic is null) return null;
        await observer.AfterComicRootsReadAsync(token);
        return Map(comic,
            await db.ComicGenres.AsNoTracking().Where(x => x.ComicId == id).ToListAsync(token),
            await db.ComicAuthors.AsNoTracking().Where(x => x.ComicId == id).ToListAsync(token),
            await db.ComicTranslationGroups.AsNoTracking().Where(x => x.ComicId == id).ToListAsync(token));
    }, token);

    public async Task<CatalogAdministrationResult<AdminComicItem>> SaveComicAsync(
        SaveComicInput input, Guid actorId, string correlationId, DateTimeOffset now,
        CancellationToken token)
    {
        if (!ValidComic(input, actorId, correlationId)) return Invalid<AdminComicItem>();
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        try
        {
            if (!await LockTaxonomyReferences(input, token)) return Invalid<AdminComicItem>();
            Comic comic;
            if (input.Id is null)
            {
                now = await ServerNow(token);
                comic = Comic.Create(Guid.NewGuid(), input.Slug.Trim(), input.Title.Trim(),
                    input.Description.Trim(), input.SeriesStatus, input.CoverAssetId?.Trim(),
                    input.DefaultPriceCoins, input.DefaultVipEligible, now);
                db.Comics.Add(comic);
            }
            else
            {
                comic = await db.Comics.FromSqlInterpolated(
                    $"SELECT * FROM catalog.comics WHERE id = {input.Id.Value} FOR UPDATE")
                    .SingleOrDefaultAsync(token) ?? null!;
                if (comic is null) return Missing<AdminComicItem>();
                await observer.AfterResourceLockedAsync("Comic", comic.Id, token);
                if (input.ExpectedVersion is null) return Invalid<AdminComicItem>();
                now = await ServerNow(token);
                try
                {
                    comic.UpdateEditorState(input.Slug.Trim(), input.Title, input.Description, input.SeriesStatus,
                        input.CoverAssetId, input.DefaultPriceCoins, input.DefaultVipEligible,
                        input.ExpectedVersion.Value, now);
                }
                catch (CatalogRuleViolationException ex)
                {
                    return ex.Violation == CatalogRuleViolation.ConcurrencyConflict
                        ? Conflict<AdminComicItem>()
                        : new(CatalogAdministrationOutcome.PublicationPreconditionFailed);
                }
            }

            await ReplaceLinks(comic.Id, input, token);
            AddAudit(actorId, input.Id is null ? "Catalog.ComicCreated" : "Catalog.ComicUpdated",
                "Comic", comic.Id, correlationId, now);
            await db.SaveChangesAsync(token);
            var response = Map(comic,
                input.GenreIds.Distinct().Select(id => ComicGenre.Create(comic.Id, id)),
                input.AuthorIds.Distinct().Select(id => ComicAuthor.Create(comic.Id, id)),
                input.TranslationGroupIds.Distinct().Select(id => ComicTranslationGroup.Create(comic.Id, id)));
            await transaction.CommitAsync(token);
            return new(CatalogAdministrationOutcome.Succeeded, response);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ux_comics_slug" })
        {
            await transaction.RollbackAsync(token);
            return new(CatalogAdministrationOutcome.SlugConflict);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.ForeignKeyViolation })
        {
            await transaction.RollbackAsync(token);
            return Invalid<AdminComicItem>();
        }
    }

    public async Task<AdminCatalogPage<AdminChapterItem>> ListChaptersAsync(
        Guid? comicId, string? search, string? publicationStatus, int page, int pageSize,
        CancellationToken token)
    {
        var query = db.Chapters.AsNoTracking().Where(x => !comicId.HasValue || x.ComicId == comicId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(x => EF.Functions.ILike(x.ChapterLabel, $"%{term}%"));
        }
        if (!string.IsNullOrWhiteSpace(publicationStatus))
            query = query.Where(x => x.PublicationStatus == publicationStatus);
        var total = await query.CountAsync(token);
        var items = await query.OrderBy(x => x.ComicId).ThenBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).Select(x => Map(x)).ToListAsync(token);
        return new(items, page, pageSize, total,
            total == 0 ? 0 : (int)Math.Ceiling(total / (double)pageSize));
    }

    public async Task<AdminChapterItem?> GetChapterAsync(Guid id, CancellationToken token) =>
        await db.Chapters.AsNoTracking().Where(x => x.Id == id).Select(x => Map(x)).SingleOrDefaultAsync(token);

    public async Task<CatalogAdministrationResult<AdminChapterItem>> SaveChapterAsync(
        SaveChapterInput input, Guid actorId, string correlationId, DateTimeOffset now,
        CancellationToken token)
    {
        if (input.ComicId == Guid.Empty || string.IsNullOrWhiteSpace(input.ChapterLabel) ||
            input.ChapterLabel.Length > Chapter.MaximumLabelLength || input.PriceOverrideCoins < 0 ||
            actorId == Guid.Empty || string.IsNullOrWhiteSpace(correlationId)) return Invalid<AdminChapterItem>();
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        try
        {
            if (!await db.Comics.AnyAsync(x => x.Id == input.ComicId, token)) return Missing<AdminChapterItem>();
            Chapter chapter;
            if (input.Id is null)
            {
                now = await ServerNow(token);
                chapter = Chapter.Create(Guid.NewGuid(), input.ComicId, input.ChapterLabel.Trim(),
                    input.SortOrder, input.PriceOverrideCoins, input.VipOverride, now);
                db.Chapters.Add(chapter);
            }
            else
            {
                chapter = await db.Chapters.FromSqlInterpolated(
                    $"SELECT * FROM catalog.chapters WHERE id = {input.Id.Value} FOR UPDATE")
                    .SingleOrDefaultAsync(token) ?? null!;
                if (chapter is null) return Missing<AdminChapterItem>();
                await observer.AfterResourceLockedAsync("Chapter", chapter.Id, token);
                if (chapter.ComicId != input.ComicId || input.ExpectedVersion is null) return Invalid<AdminChapterItem>();
                now = await ServerNow(token);
                try { chapter.UpdateMetadata(input.ChapterLabel, input.SortOrder, input.PriceOverrideCoins,
                    input.VipOverride, input.ExpectedVersion.Value, now); }
                catch (CatalogRuleViolationException) { return Conflict<AdminChapterItem>(); }
            }
            AddAudit(actorId, input.Id is null ? "Catalog.ChapterCreated" : "Catalog.ChapterUpdated",
                "Chapter", chapter.Id, correlationId, now);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return new(CatalogAdministrationOutcome.Succeeded, Map(chapter));
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            await transaction.RollbackAsync(token);
            return Conflict<AdminChapterItem>();
        }
    }

    public async Task<IReadOnlyList<CatalogOption>> ListTaxonomyAsync(string kind, CancellationToken token) => kind switch
    {
        "genres" => await db.Genres.AsNoTracking().OrderBy(x => x.Name).Select(x => new CatalogOption(x.Id, x.Name, x.Slug)).ToListAsync(token),
        "authors" => await db.Authors.AsNoTracking().OrderBy(x => x.Name).Select(x => new CatalogOption(x.Id, x.Name)).ToListAsync(token),
        "translation-groups" => await db.TranslationGroups.AsNoTracking().OrderBy(x => x.Name).Select(x => new CatalogOption(x.Id, x.Name)).ToListAsync(token),
        _ => [],
    };

    public async Task<CatalogAdministrationResult<CatalogOption>> CreateTaxonomyAsync(
        string kind, string name, string? slug, Guid actorId, string correlationId,
        DateTimeOffset now, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(name) || actorId == Guid.Empty) return Invalid<CatalogOption>();
        await using var tx = await db.Database.BeginTransactionAsync(token);
        now = await ServerNow(token);
        var id = Guid.NewGuid();
        CatalogOption option;
        switch (kind)
        {
            case "genres" when !string.IsNullOrWhiteSpace(slug):
                db.Genres.Add(Genre.Create(id, slug.Trim(), name.Trim())); option = new(id, name.Trim(), slug.Trim()); break;
            case "authors": db.Authors.Add(Author.Create(id, name.Trim())); option = new(id, name.Trim()); break;
            case "translation-groups": db.TranslationGroups.Add(TranslationGroup.Create(id, name.Trim())); option = new(id, name.Trim()); break;
            default: return Invalid<CatalogOption>();
        }
        AddAudit(actorId, "Catalog.TaxonomyCreated", "Taxonomy", id, correlationId, now);
        try { await db.SaveChangesAsync(token); await tx.CommitAsync(token); return new(CatalogAdministrationOutcome.Succeeded, option); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" }) { return Conflict<CatalogOption>(); }
    }

    public async Task<CatalogAdministrationResult<CatalogOption>> UpdateTaxonomyAsync(
        string kind, Guid id, string name, string? slug, Guid actorId, string correlationId,
        CancellationToken token)
    {
        if (id == Guid.Empty || string.IsNullOrWhiteSpace(name) || actorId == Guid.Empty)
            return Invalid<CatalogOption>();
        await using var tx = await db.Database.BeginTransactionAsync(token);
        CatalogOption option;
        if (kind == "genres")
        {
            if (string.IsNullOrWhiteSpace(slug)) return Invalid<CatalogOption>();
            var item = await db.Genres.FromSqlInterpolated($"SELECT * FROM catalog.genres WHERE id={id} FOR UPDATE").SingleOrDefaultAsync(token);
            if (item is null) return Missing<CatalogOption>(); await observer.AfterResourceLockedAsync("Genre", id, token); item.Update(slug, name); option = new(id, name.Trim(), slug.Trim());
        }
        else if (kind == "authors")
        {
            var item = await db.Authors.FromSqlInterpolated($"SELECT * FROM catalog.authors WHERE id={id} FOR UPDATE").SingleOrDefaultAsync(token);
            if (item is null) return Missing<CatalogOption>(); await observer.AfterResourceLockedAsync("Author", id, token); item.Update(name); option = new(id, name.Trim());
        }
        else if (kind == "translation-groups")
        {
            var item = await db.TranslationGroups.FromSqlInterpolated($"SELECT * FROM catalog.translation_groups WHERE id={id} FOR UPDATE").SingleOrDefaultAsync(token);
            if (item is null) return Missing<CatalogOption>(); await observer.AfterResourceLockedAsync("TranslationGroup", id, token); item.Update(name); option = new(id, name.Trim());
        }
        else return Invalid<CatalogOption>();
        var now = await ServerNow(token); AddAudit(actorId, "Catalog.TaxonomyUpdated", "Taxonomy", id, correlationId, now);
        try { await db.SaveChangesAsync(token); await tx.CommitAsync(token); return new(CatalogAdministrationOutcome.Succeeded, option); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" }) { return Conflict<CatalogOption>(); }
    }

    public async Task<CatalogAdministrationResult<CatalogOption>> DeleteTaxonomyAsync(
        string kind, Guid id, Guid actorId, string correlationId, CancellationToken token)
    {
        if (id == Guid.Empty || actorId == Guid.Empty) return Invalid<CatalogOption>();
        await using var tx = await db.Database.BeginTransactionAsync(token);
        CatalogOption option;
        if (kind == "genres") { var x=await db.Genres.FromSqlInterpolated($"SELECT * FROM catalog.genres WHERE id={id} FOR UPDATE").SingleOrDefaultAsync(token); if(x is null)return Missing<CatalogOption>(); option=new(id,x.Name,x.Slug); db.Genres.Remove(x); }
        else if(kind=="authors") { var x=await db.Authors.FromSqlInterpolated($"SELECT * FROM catalog.authors WHERE id={id} FOR UPDATE").SingleOrDefaultAsync(token); if(x is null)return Missing<CatalogOption>(); option=new(id,x.Name); db.Authors.Remove(x); }
        else if(kind=="translation-groups") { var x=await db.TranslationGroups.FromSqlInterpolated($"SELECT * FROM catalog.translation_groups WHERE id={id} FOR UPDATE").SingleOrDefaultAsync(token); if(x is null)return Missing<CatalogOption>(); option=new(id,x.Name); db.TranslationGroups.Remove(x); }
        else return Invalid<CatalogOption>();
        AddAudit(actorId,"Catalog.TaxonomyDeleted","Taxonomy",id,correlationId,await ServerNow(token));
        try
        {
            await db.SaveChangesAsync(token);
            await tx.CommitAsync(token);
            return new(CatalogAdministrationOutcome.Succeeded,option);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23503" or "23505" })
        {
            return Conflict<CatalogOption>();
        }
    }

    private async Task ReplaceLinks(Guid comicId, SaveComicInput input, CancellationToken token)
    {
        db.ComicGenres.RemoveRange(await db.ComicGenres.Where(x => x.ComicId == comicId).ToListAsync(token));
        db.ComicAuthors.RemoveRange(await db.ComicAuthors.Where(x => x.ComicId == comicId).ToListAsync(token));
        db.ComicTranslationGroups.RemoveRange(await db.ComicTranslationGroups.Where(x => x.ComicId == comicId).ToListAsync(token));
        db.ComicGenres.AddRange(input.GenreIds.Distinct().Select(x => ComicGenre.Create(comicId, x)));
        db.ComicAuthors.AddRange(input.AuthorIds.Distinct().Select(x => ComicAuthor.Create(comicId, x)));
        db.ComicTranslationGroups.AddRange(input.TranslationGroupIds.Distinct().Select(x => ComicTranslationGroup.Create(comicId, x)));
    }

    private async Task<bool> LockTaxonomyReferences(SaveComicInput input, CancellationToken token)
    {
        foreach (var id in input.GenreIds.Distinct().Order())
            if (await db.Genres.FromSqlInterpolated(
                    $"SELECT * FROM catalog.genres WHERE id = {id} FOR UPDATE")
                .SingleOrDefaultAsync(token) is null) return false;
        foreach (var id in input.AuthorIds.Distinct().Order())
            if (await db.Authors.FromSqlInterpolated(
                    $"SELECT * FROM catalog.authors WHERE id = {id} FOR UPDATE")
                .SingleOrDefaultAsync(token) is null) return false;
        foreach (var id in input.TranslationGroupIds.Distinct().Order())
            if (await db.TranslationGroups.FromSqlInterpolated(
                    $"SELECT * FROM catalog.translation_groups WHERE id = {id} FOR UPDATE")
                .SingleOrDefaultAsync(token) is null) return false;
        return true;
    }

    private void AddAudit(Guid actor, string action, string type, Guid target, string correlation, DateTimeOffset now) =>
        db.AuditLogs.Add(AuditLog.Create(Guid.NewGuid(), actor, action, type, target, correlation, now, "{}"));

    private static bool ValidComic(SaveComicInput x, Guid actor, string correlation) =>
        Comic.IsValidSlug(x.Slug) && !string.IsNullOrWhiteSpace(x.Title) && x.Description is not null &&
        (x.CoverAssetId is null || !string.IsNullOrWhiteSpace(x.CoverAssetId)) &&
        ComicSeriesStatuses.IsDefined(x.SeriesStatus) && x.DefaultPriceCoins >= 0 && actor != Guid.Empty &&
        !string.IsNullOrWhiteSpace(correlation);
    private static AdminComicItem Map(Comic x, IEnumerable<ComicGenre> g, IEnumerable<ComicAuthor> a, IEnumerable<ComicTranslationGroup> t) =>
        new(x.Id, x.Slug, x.Title, x.Description, x.SeriesStatus, x.PublicationStatus, x.CoverAssetId,
            x.DefaultPriceCoins, x.DefaultVipEligible, x.Version,
            g.Where(y => y.ComicId == x.Id).Select(y => y.GenreId).ToArray(),
            a.Where(y => y.ComicId == x.Id).Select(y => y.AuthorId).ToArray(),
            t.Where(y => y.ComicId == x.Id).Select(y => y.TranslationGroupId).ToArray());
    private static AdminChapterItem Map(Chapter x) => new(x.Id, x.ComicId, x.ChapterLabel, x.SortOrder,
        x.PriceOverrideCoins, x.VipOverride, x.PublicationStatus, x.Version);
    private static CatalogAdministrationResult<T> Invalid<T>() => new(CatalogAdministrationOutcome.ValidationFailed);
    private static CatalogAdministrationResult<T> Missing<T>() => new(CatalogAdministrationOutcome.NotFound);
    private static CatalogAdministrationResult<T> Conflict<T>() => new(CatalogAdministrationOutcome.Conflict);
    private Task<DateTimeOffset> ServerNow(CancellationToken token) => db.Database
        .SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"")
        .SingleAsync(token);
}
