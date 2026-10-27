using ComicManagement.Identity.Users;
using ComicManagement.Shared.Pagination;

namespace ComicManagement.Identity.Administration;

public enum UserAdministrationOutcome
{
    Succeeded,
    ValidationFailed,
    NotFound,
    ConcurrencyConflict,
}

public sealed record AdminUserListItem(
    Guid Id,
    string DisplayName,
    string? Email,
    string? AvatarUrl,
    string Role,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long Version);

public sealed record AdminUserPage(
    IReadOnlyList<AdminUserListItem> Items,
    int Page,
    int PageSize,
    long TotalItems,
    int TotalPages);

public sealed record AdminUserWalletSummary(
    long PaidAvailable,
    long PaidReserved,
    long BonusAvailable,
    long TotalAvailable,
    long? Version);

public sealed record AdminUserMembershipSummary(
    bool IsActive,
    DateTimeOffset? ExpiresAt,
    long? Version);

public sealed record AdminUserAccessSummary(
    long FollowedComicCount,
    long UnlockedChapterCount);

public sealed record AdminUserPaymentSummary(
    Guid PaymentId,
    string OrderCode,
    string OrderType,
    long AmountVnd,
    string Status,
    DateTimeOffset CreatedAt);

public sealed record AdminUserDetail(
    AdminUserListItem User,
    AdminUserWalletSummary Wallet,
    AdminUserMembershipSummary Membership,
    AdminUserAccessSummary Access,
    IReadOnlyList<AdminUserPaymentSummary> RecentPayments);

public sealed record UserAdministrationResult(
    UserAdministrationOutcome Outcome,
    AdminUserPage? Page = null,
    AdminUserDetail? Detail = null);

public sealed record UserStatusChangeRequest(
    Guid ActorId,
    Guid UserId,
    string Status,
    long ExpectedVersion,
    string CorrelationId);

public interface IUserAdministrationStore
{
    Task<UserAdministrationResult> ListAsync(
        string? search,
        string? status,
        OffsetPage page,
        CancellationToken cancellationToken = default);

    Task<UserAdministrationResult> GetAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    Task<UserAdministrationResult> ChangeStatusAsync(
        UserStatusChangeRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class UserAdministrationHandler(IUserAdministrationStore store)
{
    public const int DefaultPageSize = 30;
    public const int MaximumPageSize = 100;
    public const int MaximumSearchLength = 200;

    public Task<UserAdministrationResult> ListAsync(
        string? search,
        string? status,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        search = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        status = string.IsNullOrWhiteSpace(status) ? null : status.Trim();
        if (search?.Length > MaximumSearchLength ||
            status is not null && status is not (UserStatuses.Active or UserStatuses.Disabled) ||
            !OffsetPage.TryCreate(page, pageSize, MaximumPageSize, out var requestedPage))
        {
            return Task.FromResult(new UserAdministrationResult(
                UserAdministrationOutcome.ValidationFailed));
        }

        return store.ListAsync(search, status, requestedPage, cancellationToken);
    }

    public Task<UserAdministrationResult> GetAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        userId == Guid.Empty
            ? Task.FromResult(new UserAdministrationResult(
                UserAdministrationOutcome.ValidationFailed))
            : store.GetAsync(userId, cancellationToken);

    public Task<UserAdministrationResult> ChangeStatusAsync(
        Guid actorId,
        Guid userId,
        string status,
        long expectedVersion,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        if (actorId == Guid.Empty || userId == Guid.Empty || expectedVersion < 1 ||
            status is not (UserStatuses.Active or UserStatuses.Disabled) ||
            string.IsNullOrWhiteSpace(correlationId))
        {
            return Task.FromResult(new UserAdministrationResult(
                UserAdministrationOutcome.ValidationFailed));
        }

        return store.ChangeStatusAsync(
            new UserStatusChangeRequest(
                actorId,
                userId,
                status,
                expectedVersion,
                correlationId),
            cancellationToken);
    }
}
