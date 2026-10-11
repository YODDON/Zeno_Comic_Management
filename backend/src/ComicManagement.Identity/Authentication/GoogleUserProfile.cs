namespace ComicManagement.Identity.Authentication;

public sealed record GoogleUserProfile(
    string Subject,
    string DisplayName,
    string? Email,
    string? AvatarUrl);
