namespace ComicManagement.Catalog.Taxonomy;

public sealed class Author
{
    private Author()
    {
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public static Author Create(Guid id, string name)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Author ID is required.", nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return new Author
        {
            Id = id,
            Name = name,
        };
    }
    public void Update(string name) { ArgumentException.ThrowIfNullOrWhiteSpace(name); Name = name.Trim(); }
}
