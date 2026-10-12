using System.Text;
using ComicManagement.Identity.Users;
using ComicManagement.Shared.Time;

namespace ComicManagement.Identity.Authentication;

public sealed class GoogleSignInHandler(
    IGoogleIdentityStore identityStore,
    IClock clock)
{
    private const int MaximumSaveAttempts = 3;

    public async Task<GoogleSignInResult> HandleAsync(
        GoogleUserProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var normalizedProfile = Normalize(profile);

        for (var attempt = 0; attempt < MaximumSaveAttempts; attempt++)
        {
            var existingUser = await identityStore.FindByGoogleSubjectAsync(
                normalizedProfile.Subject,
                cancellationToken);

            if (existingUser is not null)
            {
                if (!existingUser.IsActive)
                {
                    return GoogleSignInResult.Disabled();
                }

                existingUser.RefreshProviderAttributes(
                    normalizedProfile.Email,
                    normalizedProfile.AvatarUrl,
                    clock.UtcNow);

                var updateOutcome = await identityStore.SaveChangesAsync(cancellationToken);
                if (updateOutcome == IdentityStoreSaveOutcome.Succeeded)
                {
                    return GoogleSignInResult.Succeeded(ToAuthenticatedUser(existingUser));
                }

                continue;
            }

            var now = clock.UtcNow;
            var user = User.CreateFromGoogle(
                Guid.NewGuid(),
                normalizedProfile.DisplayName,
                normalizedProfile.Email,
                normalizedProfile.AvatarUrl,
                now);
            var googleIdentity = GoogleIdentity.Create(
                user.Id,
                normalizedProfile.Subject,
                now);

            identityStore.Add(user, googleIdentity);
            var createOutcome = await identityStore.SaveChangesAsync(cancellationToken);

            if (createOutcome == IdentityStoreSaveOutcome.Succeeded)
            {
                return GoogleSignInResult.Succeeded(ToAuthenticatedUser(user));
            }
        }

        throw new IdentityConcurrencyException();
    }

    private static GoogleUserProfile Normalize(GoogleUserProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Subject) ||
            profile.Subject.Length > GoogleIdentity.MaximumSubjectLength ||
            !string.Equals(profile.Subject, profile.Subject.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("Google subject is invalid.", nameof(profile));
        }

        if (string.IsNullOrWhiteSpace(profile.DisplayName))
        {
            throw new ArgumentException("Google display name is required.", nameof(profile));
        }

        var email = string.IsNullOrWhiteSpace(profile.Email)
            ? null
            : profile.Email.Trim();

        if (email?.Length > User.MaximumEmailLength)
        {
            throw new ArgumentException("Google email is too long.", nameof(profile));
        }

        string? avatarUrl = null;
        if (!string.IsNullOrWhiteSpace(profile.AvatarUrl))
        {
            if (!Uri.TryCreate(profile.AvatarUrl, UriKind.Absolute, out var avatarUri) ||
                avatarUri.Scheme != Uri.UriSchemeHttps)
            {
                throw new ArgumentException("Google avatar URL must use HTTPS.", nameof(profile));
            }

            avatarUrl = avatarUri.AbsoluteUri;
        }

        return profile with
        {
            DisplayName = TruncateRunes(profile.DisplayName.Trim(), User.MaximumDisplayNameLength),
            Email = email,
            AvatarUrl = avatarUrl,
        };
    }

    private static string TruncateRunes(string value, int maximumRuneCount)
    {
        var builder = new StringBuilder(value.Length);
        var count = 0;

        foreach (var rune in value.EnumerateRunes())
        {
            if (count == maximumRuneCount)
            {
                break;
            }

            builder.Append(rune.ToString());
            count++;
        }

        return builder.ToString();
    }

    private static AuthenticatedUser ToAuthenticatedUser(User user) => new(
        user.Id,
        user.DisplayName,
        user.Email,
        user.AvatarUrl,
        user.Role,
        user.Status,
        user.Version);
}
