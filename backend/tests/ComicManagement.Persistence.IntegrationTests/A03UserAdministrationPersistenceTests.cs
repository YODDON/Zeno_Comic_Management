using ComicManagement.Administration.Auditing;
using ComicManagement.Identity.Administration;
using ComicManagement.Identity.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace ComicManagement.Persistence.IntegrationTests;

public sealed class A03UserAdministrationPersistenceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18.0-alpine3.22")
        .WithDatabase("a03_users").WithUsername("postgres").WithPassword("postgres").Build();
    private ServiceProvider _services = null!;
    private readonly Guid _adminId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _services = new ServiceCollection().AddPersistence(_postgres.GetConnectionString()).BuildServiceProvider(true);
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        db.AddRange(
            User.CreateFromGoogle(_adminId, "Admin", "admin@example.test", null, now),
            User.CreateFromGoogle(_userId, "Reader", "reader@example.test", null, now));
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE identity.users SET role = 'Admin' WHERE id = {_adminId}");
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Disable_is_versioned_audited_and_immediately_visible_to_admin_reads()
    {
        await using var scope = _services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<UserAdministrationHandler>();

        var initial = await handler.GetAsync(_userId);
        var changed = await handler.ChangeStatusAsync(
            _adminId, _userId, UserStatuses.Disabled, initial.Detail!.User.Version, "a03-test");

        Assert.Equal(UserAdministrationOutcome.Succeeded, changed.Outcome);
        Assert.Equal(UserStatuses.Disabled, changed.Detail!.User.Status);
        Assert.Equal(initial.Detail.User.Version + 1, changed.Detail.User.Version);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var audit = await db.Set<AuditLog>().AsNoTracking().SingleAsync(item => item.TargetId == _userId);
        Assert.Equal("User.Disabled", audit.Action);
        Assert.DoesNotContain("reader@example.test", audit.RedactedChanges, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stale_version_fails_without_status_or_audit_mutation()
    {
        await using var scope = _services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<UserAdministrationHandler>();
        var result = await handler.ChangeStatusAsync(
            _adminId, _userId, UserStatuses.Disabled, 999, "a03-stale");

        Assert.Equal(UserAdministrationOutcome.ConcurrencyConflict, result.Outcome);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(UserStatuses.Active, (await db.Set<User>().AsNoTracking().SingleAsync(item => item.Id == _userId)).Status);
        Assert.False(await db.Set<AuditLog>().AnyAsync(item => item.CorrelationId == "a03-stale"));
    }

    [Fact]
    public async Task Status_change_waits_for_the_user_lock_then_revalidates_version()
    {
        await using var blocker = new NpgsqlConnection(_postgres.GetConnectionString());
        await blocker.OpenAsync();
        await using var blockerTransaction = await blocker.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand(
            "SELECT id FROM identity.users WHERE id = @id FOR UPDATE",
            blocker,
            blockerTransaction))
        {
            lockCommand.Parameters.AddWithValue("id", _userId);
            await lockCommand.ExecuteScalarAsync();
        }

        var contender = Task.Run(async () =>
        {
            await using var scope = _services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<UserAdministrationHandler>()
                .ChangeStatusAsync(
                    _adminId,
                    _userId,
                    UserStatuses.Disabled,
                    1,
                    "a03-contended");
        });

        Assert.NotSame(contender, await Task.WhenAny(contender, Task.Delay(TimeSpan.FromMilliseconds(250))));
        await using (var changeCommand = new NpgsqlCommand(
            "UPDATE identity.users SET version = version + 1, updated_at = clock_timestamp() WHERE id = @id",
            blocker,
            blockerTransaction))
        {
            changeCommand.Parameters.AddWithValue("id", _userId);
            Assert.Equal(1, await changeCommand.ExecuteNonQueryAsync());
        }
        await blockerTransaction.CommitAsync();

        var result = await contender;
        Assert.Equal(UserAdministrationOutcome.ConcurrencyConflict, result.Outcome);
        await using var assertionScope = _services.CreateAsyncScope();
        var db = assertionScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(UserStatuses.Active,
            (await db.Set<User>().AsNoTracking().SingleAsync(item => item.Id == _userId)).Status);
        Assert.False(await db.Set<AuditLog>().AnyAsync(item => item.CorrelationId == "a03-contended"));
    }
}
