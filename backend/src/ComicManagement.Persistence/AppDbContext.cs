using ComicManagement.Catalog.Chapters;
using ComicManagement.Catalog.Comics;
using ComicManagement.Catalog.Taxonomy;
using ComicManagement.Identity.Users;
using Microsoft.EntityFrameworkCore;

namespace ComicManagement.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    internal DbSet<User> Users => Set<User>();

    internal DbSet<GoogleIdentity> GoogleIdentities => Set<GoogleIdentity>();

    internal DbSet<Comic> Comics => Set<Comic>();

    internal DbSet<Chapter> Chapters => Set<Chapter>();

    internal DbSet<ChapterPage> ChapterPages => Set<ChapterPage>();

    internal DbSet<Genre> Genres => Set<Genre>();

    internal DbSet<Author> Authors => Set<Author>();

    internal DbSet<TranslationGroup> TranslationGroups => Set<TranslationGroup>();

    internal DbSet<ComicGenre> ComicGenres => Set<ComicGenre>();

    internal DbSet<ComicAuthor> ComicAuthors => Set<ComicAuthor>();

    internal DbSet<ComicTranslationGroup> ComicTranslationGroups => Set<ComicTranslationGroup>();

    internal DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    internal DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
