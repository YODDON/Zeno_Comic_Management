namespace ComicManagement.Catalog.Taxonomy;

public sealed class Genre
{
    public const int MaximumSlugLength = 200;

    private Genre()
    {
    }

    public Guid Id { get; private set; }

    public string Slug { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public static Genre Create(Guid id, string slug, string name)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Genre ID is required.", nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        if (slug.Length > MaximumSlugLength)
        {
            throw new ArgumentOutOfRangeException(nameof(slug));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return new Genre
        {
            Id = id,
            Slug = slug,
            Name = name,
        };
    }
    public void Update(string slug, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug); ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (slug.Length > MaximumSlugLength) throw new ArgumentOutOfRangeException(nameof(slug));
        Slug = slug.Trim(); Name = name.Trim();
    }
}
