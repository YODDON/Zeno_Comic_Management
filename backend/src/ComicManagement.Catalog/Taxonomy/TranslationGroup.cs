namespace ComicManagement.Catalog.Taxonomy;

public sealed class TranslationGroup
{
    private TranslationGroup()
    {
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public static TranslationGroup Create(Guid id, string name)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Translation group ID is required.", nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return new TranslationGroup
        {
            Id = id,
            Name = name,
        };
    }
    public void Update(string name) { ArgumentException.ThrowIfNullOrWhiteSpace(name); Name = name.Trim(); }
}
