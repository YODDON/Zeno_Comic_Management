namespace ComicManagement.Catalog;

public enum CatalogRuleViolation
{
    ConcurrencyConflict = 1,
    PublicationPreconditionFailed = 2,
    SlugLocked = 3,
}

public sealed class CatalogRuleViolationException(
    CatalogRuleViolation violation,
    string message) : Exception(message)
{
    public CatalogRuleViolation Violation { get; } = violation;
}
