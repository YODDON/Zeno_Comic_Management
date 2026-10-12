using System.Text;

namespace ComicManagement.Identity.Users;

public sealed class User
{
    public const int MaximumDisplayNameLength = 120;
    public const int MaximumEmailLength = 320;

    private User()
    {
    }

    public Guid Id { get; private set; }

    public string DisplayName { get; private set; } = string.Empty;

    public string? Email { get; private set; }

    public string? AvatarUrl { get; private set; }

    public string Role { get; private set; } = IdentityRoles.User;

    public string Status { get; private set; } = UserStatuses.Active;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public long Version { get; private set; }

    public bool IsActive => string.Equals(Status, UserStatuses.Active, StringComparison.Ordinal);

    public static User CreateFromGoogle(
        Guid id,
        string displayName,
        string? email,
        string? avatarUrl,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("User ID is required.", nameof(id));
        }

        ValidateDisplayName(displayName);
        ValidateProviderAttributes(email, avatarUrl);
        EnsureUtc(createdAt, nameof(createdAt));

        return new User
        {
            Id = id,
            DisplayName = displayName,
            Email = email,
            AvatarUrl = avatarUrl,
            Role = IdentityRoles.User,
            Status = UserStatuses.Active,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            Version = 1,
        };
    }

    public void RefreshProviderAttributes(
        string? email,
        string? avatarUrl,
        DateTimeOffset updatedAt)
    {
        ValidateProviderAttributes(email, avatarUrl);
        EnsureUtc(updatedAt, nameof(updatedAt));

        if (string.Equals(Email, email, StringComparison.Ordinal) &&
            string.Equals(AvatarUrl, avatarUrl, StringComparison.Ordinal))
        {
            return;
        }

        Email = email;
        AvatarUrl = avatarUrl;
        UpdatedAt = updatedAt;
        Version = checked(Version + 1);
    }

    public bool UpdateDisplayName(
        string displayName,
        DateTimeOffset updatedAt)
    {
        ValidateDisplayName(displayName);
        EnsureUtc(updatedAt, nameof(updatedAt));

        if (!string.Equals(DisplayName, displayName, StringComparison.Ordinal))
        {
            DisplayName = displayName;
            UpdatedAt = updatedAt;
            Version = checked(Version + 1);
            return true;
        }

        return false;
    }

    private static void ValidateDisplayName(string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (displayName.EnumerateRunes().Count() > MaximumDisplayNameLength)
        {
            throw new ArgumentOutOfRangeException(nameof(displayName));
        }
    }

    private static void ValidateProviderAttributes(string? email, string? avatarUrl)
    {
        if (email?.Length > MaximumEmailLength)
        {
            throw new ArgumentOutOfRangeException(nameof(email));
        }

        if (avatarUrl is not null &&
            (!Uri.TryCreate(avatarUrl, UriKind.Absolute, out var uri) ||
             uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Avatar URL must use HTTPS.", nameof(avatarUrl));
        }
    }

    private static void EnsureUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must use the UTC offset.", parameterName);
        }
    }
}
