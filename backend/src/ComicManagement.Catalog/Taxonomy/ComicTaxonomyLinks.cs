namespace ComicManagement.Catalog.Taxonomy;

public sealed class ComicGenre
{
    private ComicGenre()
    {
    }

    public Guid ComicId { get; private set; }

    public Guid GenreId { get; private set; }

    public static ComicGenre Create(Guid comicId, Guid genreId)
    {
        ValidateIds(comicId, genreId, nameof(genreId));
        return new ComicGenre { ComicId = comicId, GenreId = genreId };
    }

    private static void ValidateIds(Guid comicId, Guid dimensionId, string dimensionParameterName)
    {
        if (comicId == Guid.Empty)
        {
            throw new ArgumentException("Comic ID is required.", nameof(comicId));
        }

        if (dimensionId == Guid.Empty)
        {
            throw new ArgumentException("Taxonomy ID is required.", dimensionParameterName);
        }
    }
}

public sealed class ComicAuthor
{
    private ComicAuthor()
    {
    }

    public Guid ComicId { get; private set; }

    public Guid AuthorId { get; private set; }

    public static ComicAuthor Create(Guid comicId, Guid authorId)
    {
        ValidateIds(comicId, authorId);
        return new ComicAuthor { ComicId = comicId, AuthorId = authorId };
    }

    private static void ValidateIds(Guid comicId, Guid authorId)
    {
        if (comicId == Guid.Empty)
        {
            throw new ArgumentException("Comic ID is required.", nameof(comicId));
        }

        if (authorId == Guid.Empty)
        {
            throw new ArgumentException("Author ID is required.", nameof(authorId));
        }
    }
}

public sealed class ComicTranslationGroup
{
    private ComicTranslationGroup()
    {
    }

    public Guid ComicId { get; private set; }

    public Guid TranslationGroupId { get; private set; }

    public static ComicTranslationGroup Create(Guid comicId, Guid translationGroupId)
    {
        ValidateIds(comicId, translationGroupId);
        return new ComicTranslationGroup
        {
            ComicId = comicId,
            TranslationGroupId = translationGroupId,
        };
    }

    private static void ValidateIds(Guid comicId, Guid translationGroupId)
    {
        if (comicId == Guid.Empty)
        {
            throw new ArgumentException("Comic ID is required.", nameof(comicId));
        }

        if (translationGroupId == Guid.Empty)
        {
            throw new ArgumentException("Translation group ID is required.", nameof(translationGroupId));
        }
    }
}
