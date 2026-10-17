using System.Data;
using System.Text.Json;
using ComicManagement.Administration.Auditing;
using ComicManagement.Billing.Payments;
using ComicManagement.Membership.Memberships;
using ComicManagement.Membership.Plans;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ComicManagement.Persistence;

internal interface IVipCheckoutCriticalSectionObserver
{
    Task AfterLockAcquiredAsync(
        Guid userId,
        string idempotencyKey,
        CancellationToken cancellationToken);
}

internal sealed class NoOpVipCheckoutCriticalSectionObserver :
    IVipCheckoutCriticalSectionObserver
{
    public Task AfterLockAcquiredAsync(
        Guid userId,
        string idempotencyKey,
        CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class VipMembershipStore(
    AppDbContext dbContext,
    IVipCheckoutCriticalSectionObserver checkoutObserver) : IVipMembershipStore
{
    public async Task<VipMembershipResult> GetPlanAsync(
        CancellationToken cancellationToken = default)
    {
        var plan = await dbContext.VipPlans
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.IsActive, cancellationToken);
        return plan is null
            ? NotFound()
            : Succeeded(plan);
    }

    public async Task<VipMembershipResult> GetMembershipAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var state = await dbContext.VipSubscriptions
            .AsNoTracking()
            .Where(subscription => subscription.UserId == userId)
            .Select(subscription => new VipMembershipState(
                subscription.ExpiresAt > DateTimeOffset.UtcNow,
                subscription.ExpiresAt))
            .SingleOrDefaultAsync(cancellationToken);
        return new VipMembershipResult(
            VipMembershipOutcome.Succeeded,
            Membership: state ?? new VipMembershipState(false, null));
    }

    public async Task<VipMembershipResult> CreateOrReplayCheckoutAsync(
        CreateVipCheckoutStoreRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        await AcquireCheckoutLockAsync(request, cancellationToken);
        await checkoutObserver.AfterLockAcquiredAsync(
            request.UserId,
            request.IdempotencyKey,
            cancellationToken);

        var existing = await dbContext.PaymentOrders
            .FromSqlInterpolated($"""
                SELECT *
                FROM billing.payment_orders
                WHERE user_id = {request.UserId}
                  AND order_type = {PaymentOrderTypes.VipPurchase}
                  AND idempotency_key = {request.IdempotencyKey}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            var replayNow = await GetAuthoritativeServerTimeAsync(cancellationToken);
            if (existing.ExpireIfDue(replayNow))
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return existing.RequestFingerprint == request.RequestFingerprint
                ? Succeeded(existing)
                : new VipMembershipResult(VipMembershipOutcome.IdempotencyConflict);
        }

        var plan = await dbContext.VipPlans
            .FromSqlInterpolated($"""
                SELECT *
                FROM membership.vip_plans
                WHERE id = {request.PlanId}
                  AND is_active
                FOR SHARE
                """)
            .SingleOrDefaultAsync(cancellationToken);
        if (plan is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return NotFound();
        }

        var serverNow = await GetAuthoritativeServerTimeAsync(cancellationToken);
        var order = PaymentOrder.CreateVipPurchase(
            request.OrderId,
            request.UserId,
            $"ZENO-VIP-{request.OrderId:N}",
            plan.PriceVnd,
            plan.Id,
            plan.DurationHours,
            plan.DiscountPercent,
            request.IdempotencyKey,
            request.RequestFingerprint,
            serverNow,
            serverNow.Add(request.TimeToLive));
        dbContext.PaymentOrders.Add(order);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Succeeded(order);
    }

    public async Task<VipMembershipResult> UpdateFutureOrderPriceAsync(
        UpdateVipPlanPriceStoreRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        var plan = await dbContext.VipPlans
            .FromSqlRaw("""
                SELECT *
                FROM membership.vip_plans
                WHERE is_active
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken);
        if (plan is null)
        {
            return NotFound();
        }

        var serverNow = await GetAuthoritativeServerTimeAsync(cancellationToken);
        var previousPrice = plan.PriceVnd;
        try
        {
            plan.ChangeFutureOrderPrice(
                request.PriceVnd,
                request.ExpectedVersion,
                serverNow);
        }
        catch (VipPlanConcurrencyException)
        {
            return new VipMembershipResult(VipMembershipOutcome.ConcurrencyConflict);
        }

        dbContext.AuditLogs.Add(AuditLog.Create(
            Guid.NewGuid(),
            request.ActorId,
            "Membership.VipPlanPriceChanged",
            "VipPlan",
            plan.Id,
            request.CorrelationId,
            serverNow,
            JsonSerializer.Serialize(new
            {
                Before = new { PriceVnd = previousPrice, Version = request.ExpectedVersion },
                After = new { plan.PriceVnd, plan.Version },
            })));
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Succeeded(plan);
    }

    private async Task AcquireCheckoutLockAsync(
        CreateVipCheckoutStoreRequest request,
        CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException("A checkout lock requires a transaction.");
        command.CommandText =
            "SELECT pg_advisory_xact_lock(hashtextextended(@lock_key, 0));";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "lock_key";
        parameter.Value = string.Concat(
            request.UserId.ToString("N"),
            ":",
            PaymentOrderTypes.VipPurchase,
            ":",
            request.IdempotencyKey);
        command.Parameters.Add(parameter);
        await command.ExecuteScalarAsync(cancellationToken);
    }

    private async Task<DateTimeOffset> GetAuthoritativeServerTimeAsync(
        CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException("Server time requires a transaction.");
        command.CommandText = "SELECT clock_timestamp();";
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value switch
        {
            DateTimeOffset timestamp => timestamp.ToUniversalTime(),
            DateTime timestamp => new DateTimeOffset(timestamp.ToUniversalTime()),
            _ => throw new InvalidOperationException("PostgreSQL did not return server time."),
        };
    }

    private static VipMembershipResult Succeeded(VipPlan plan) => new(
        VipMembershipOutcome.Succeeded,
        Plan: new VipPlanState(
            plan.Id,
            plan.PriceVnd,
            plan.DurationHours,
            plan.DiscountPercent,
            plan.Version));

    private static VipMembershipResult Succeeded(PaymentOrder order) => new(
        VipMembershipOutcome.Succeeded,
        Checkout: new VipCheckoutState(
            order.Id,
            order.AmountVnd,
            order.VipDurationHoursSnapshot ?? throw new InvalidOperationException(
                "A VIP order requires a duration snapshot."),
            order.OrderStatus,
            order.ExpiresAt));

    private static VipMembershipResult NotFound() =>
        new(VipMembershipOutcome.NotFound);
}
