namespace ComicManagement.Identity.Authentication;

public sealed record AuthenticatedUser(
    Guid Id,
    string DisplayName,
    string? Email,
    string? AvatarUrl,
    string Role,
    string Status,
    long Version);
