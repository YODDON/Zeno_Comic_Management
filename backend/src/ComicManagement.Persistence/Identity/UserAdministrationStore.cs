using System.Data;
using System.Text.Json;
using ComicManagement.Administration.Auditing;
using ComicManagement.Billing.Payments;
using ComicManagement.Identity.Administration;
using ComicManagement.Identity.Users;
using ComicManagement.Shared.Pagination;
using Microsoft.EntityFrameworkCore;

namespace ComicManagement.Persistence;

internal sealed class UserAdministrationStore(AppDbContext dbContext)
    : IUserAdministrationStore
{
    public async Task<UserAdministrationResult> ListAsync(
        string? search,
        string? status,
        OffsetPage page,
        CancellationToken cancellationToken = default)
    {
        var query = dbContext.Users.AsNoTracking();
        if (search is not null)
        {
            var pattern = $"%{EscapeLike(search)}%";
            query = query.Where(user =>
                EF.Functions.ILike(user.DisplayName, pattern, "\\") ||
                user.Email != null && EF.Functions.ILike(user.Email, pattern, "\\"));
        }

        if (status is not null)
        {
            query = query.Where(user => user.Status == status);
        }

        var totalItems = await query.LongCountAsync(cancellationToken);
        var users = await query
            .OrderByDescending(user => user.CreatedAt)
            .ThenByDescending(user => user.Id)
            .Skip(page.Offset)
            .Take(page.PageSize)
            .ToListAsync(cancellationToken);

        return new UserAdministrationResult(
            UserAdministrationOutcome.Succeeded,
            Page: new AdminUserPage(
                users.Select(ToListItem).ToArray(),
                page.Page,
                page.PageSize,
                totalItems,
                page.TotalPages(totalItems)));
    }

    public async Task<UserAdministrationResult> GetAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken);
        var result = await LoadDetailAsync(userId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<UserAdministrationResult> ChangeStatusAsync(
        UserStatusChangeRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        var user = await dbContext.Users
            .FromSqlInterpolated($"SELECT * FROM identity.users WHERE id = {request.UserId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (user is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new UserAdministrationResult(UserAdministrationOutcome.NotFound);
        }

        if (user.Version != request.ExpectedVersion)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new UserAdministrationResult(UserAdministrationOutcome.ConcurrencyConflict);
        }

        var previousStatus = user.Status;
        var serverNow = await dbContext.Database
            .SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"")
            .SingleAsync(cancellationToken);
        if (user.ChangeStatus(request.Status, serverNow))
        {
            dbContext.AuditLogs.Add(AuditLog.Create(
                Guid.NewGuid(),
                request.ActorId,
                request.Status == UserStatuses.Disabled
                    ? "User.Disabled"
                    : "User.Enabled",
                "User",
                user.Id,
                request.CorrelationId,
                serverNow,
                JsonSerializer.Serialize(new
                {
                    previousStatus,
                    status = request.Status,
                })));
        }

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            var result = await LoadDetailAsync(user.Id, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            return new UserAdministrationResult(UserAdministrationOutcome.ConcurrencyConflict);
        }
    }

    private async Task<UserAdministrationResult> LoadDetailAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var user = await dbContext.Users.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == userId, cancellationToken);
        if (user is null)
        {
            return new UserAdministrationResult(UserAdministrationOutcome.NotFound);
        }

        var wallet = await dbContext.Wallets.AsNoTracking()
            .Where(candidate => candidate.UserId == userId)
            .Select(candidate => new AdminUserWalletSummary(
                candidate.PaidAvailable,
                candidate.PaidReserved,
                candidate.BonusAvailable,
                candidate.PaidAvailable + candidate.BonusAvailable,
                candidate.Version))
            .SingleOrDefaultAsync(cancellationToken)
            ?? new AdminUserWalletSummary(0, 0, 0, 0, null);

        var serverNow = await dbContext.Database
            .SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"")
            .SingleAsync(cancellationToken);
        var membership = await dbContext.VipSubscriptions.AsNoTracking()
            .Where(candidate => candidate.UserId == userId)
            .Select(candidate => new AdminUserMembershipSummary(
                candidate.ExpiresAt > serverNow,
                candidate.ExpiresAt,
                candidate.Version))
            .SingleOrDefaultAsync(cancellationToken)
            ?? new AdminUserMembershipSummary(false, null, null);

        var access = new AdminUserAccessSummary(
            await dbContext.ComicFollows.LongCountAsync(
                candidate => candidate.UserId == userId,
                cancellationToken),
            await dbContext.ChapterUnlocks.LongCountAsync(
                candidate => candidate.UserId == userId,
                cancellationToken));

        var payments = await dbContext.PaymentOrders.AsNoTracking()
            .Where(candidate => candidate.UserId == userId)
            .OrderByDescending(candidate => candidate.CreatedAt)
            .ThenByDescending(candidate => candidate.Id)
            .Take(10)
            .Select(candidate => new AdminUserPaymentSummary(
                candidate.Id,
                candidate.OrderCode,
                candidate.OrderType,
                candidate.AmountVnd,
                candidate.OrderStatus,
                candidate.CreatedAt))
            .ToListAsync(cancellationToken);

        return new UserAdministrationResult(
            UserAdministrationOutcome.Succeeded,
            Detail: new AdminUserDetail(
                ToListItem(user),
                wallet,
                membership,
                access,
                payments));
    }

    private static AdminUserListItem ToListItem(User user) => new(
        user.Id,
        user.DisplayName,
        user.Email,
        user.AvatarUrl,
        user.Role,
        user.Status,
        user.CreatedAt,
        user.UpdatedAt,
        user.Version);

    private static string EscapeLike(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);
}
