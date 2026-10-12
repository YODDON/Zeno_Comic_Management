using System.Text;
using ComicManagement.Identity.Authentication;
using ComicManagement.Identity.Users;
using ComicManagement.Shared.Time;

namespace ComicManagement.Identity.Profiles;

public sealed class UpdateDisplayNameHandler(
    IUserProfileStore profileStore,
    IClock clock)
{
    public async Task<UpdateDisplayNameResult> HandleAsync(
        Guid userId,
        string? displayName,
        CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User ID is required.", nameof(userId));
        }

        var normalizedDisplayName = displayName?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedDisplayName) ||
            normalizedDisplayName.EnumerateRunes().Count() > User.MaximumDisplayNameLength)
        {
            return UpdateDisplayNameResult.ValidationFailed();
        }

        var user = await profileStore.FindByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return UpdateDisplayNameResult.UserNotFound();
        }

        var displayNameChanged = user.UpdateDisplayName(
            normalizedDisplayName,
            clock.UtcNow);
        if (displayNameChanged)
        {
            var saveOutcome = await profileStore.SaveChangesAsync(cancellationToken);
            if (saveOutcome == UserProfileSaveOutcome.ConcurrencyConflict)
            {
                return UpdateDisplayNameResult.ConcurrencyConflict();
            }
        }

        return UpdateDisplayNameResult.Succeeded(new AuthenticatedUser(
            user.Id,
            user.DisplayName,
            user.Email,
            user.AvatarUrl,
            user.Role,
            user.Status,
            user.Version),
            displayNameChanged);
    }
}
