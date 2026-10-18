using ComicManagement.Persistence;
using ComicManagement.Shared.Messaging;
using ComicManagement.Shared.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace ComicManagement.Persistence.IntegrationTests;

public sealed class OutboxProcessingTests : IAsyncLifetime
{
    private const string EventType = "tests.outbox-probe";
    private const string ConsumerName = "tests.outbox-probe-consumer";

    private readonly MutableClock _clock = new(new DateTimeOffset(2026, 10, 11, 5, 0, 0, TimeSpan.Zero));
    private readonly HandlerProbe _probe = new();
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18.0-alpine3.22")
        .WithDatabase("comic_management_outbox_tests")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    private ServiceProvider? _serviceProvider;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IClock>(_clock);
        services.AddSingleton(_probe);
        services.AddScoped<IOutboxMessageHandler, ProbeOutboxMessageHandler>();
        services.AddPersistence(_postgres.GetConnectionString());
        services.AddOutboxWorker(options =>
        {
            options.BatchSize = 2;
            options.PollInterval = TimeSpan.FromMilliseconds(10);
            options.LockDuration = TimeSpan.FromSeconds(30);
            options.BaseRetryDelay = TimeSpan.FromSeconds(10);
            options.MaxRetryDelay = TimeSpan.FromMinutes(1);
            options.MaxAttempts = 2;
        });

        _serviceProvider = services.BuildServiceProvider(validateScopes: true);

        await using var scope = Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await context.Database.MigrateAsync();
        await context.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE infrastructure.outbox_test_effects
            (
                message_id uuid PRIMARY KEY,
                handled_at timestamp with time zone NOT NULL
            );
            """);
    }

    [Fact]
    public async Task Hosted_worker_processes_enqueued_message()
    {
        var messageId = await EnqueueMessage();
        var worker = Services.GetServices<IHostedService>().Single();

        await worker.StartAsync(CancellationToken.None);

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            while ((await GetMessageState(messageId)).Status != "Completed")
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
            }
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        Assert.Equal(1, await CountRows(
            "infrastructure.outbox_test_effects",
            "message_id",
            messageId));
        Assert.Equal(1, await CountRows(
            "infrastructure.processed_messages",
            "message_id",
            messageId));
    }

    public async Task DisposeAsync()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
        }

        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Writer_participates_in_the_callers_transaction()
    {
        var messageId = Guid.Empty;

        await using (var scope = Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var writer = scope.ServiceProvider.GetRequiredService<IOutboxWriter>();
            await using var transaction = await context.Database.BeginTransactionAsync();

            messageId = writer.Enqueue(EventType, new { value = 1 }, _clock.UtcNow);
            await context.SaveChangesAsync();
            await transaction.RollbackAsync();
        }

        Assert.Equal(0, await CountRows(
            "infrastructure.outbox_messages",
            "id",
            messageId));
    }

    [Fact]
    public async Task Handler_failure_rolls_back_effect_and_retries_once_due()
    {
        _probe.FailuresRemaining = 1;
        var messageId = await EnqueueMessage();

        var firstAttempt = await ProcessBatch();

        Assert.Equal(new OutboxBatchResult(1, 0, 1), firstAttempt);
        var failedState = await GetMessageState(messageId);
        Assert.Equal("Pending", failedState.Status);
        Assert.Equal(1, failedState.AttemptCount);
        Assert.Equal(_clock.UtcNow.AddSeconds(10), failedState.AvailableAt);
        Assert.Equal(nameof(SimulatedHandlerFailureException), failedState.LastErrorRedacted);
        Assert.Equal(0, await CountRows(
            "infrastructure.outbox_test_effects",
            "message_id",
            messageId));
        Assert.Equal(0, await CountRows(
            "infrastructure.processed_messages",
            "message_id",
            messageId));

        Assert.Equal(0, (await ProcessBatch()).ClaimedCount);

        _clock.Advance(TimeSpan.FromSeconds(10));
        var retry = await ProcessBatch();

        Assert.Equal(new OutboxBatchResult(1, 1, 0), retry);
        var completedState = await GetMessageState(messageId);
        Assert.Equal("Completed", completedState.Status);
        Assert.Equal(2, completedState.AttemptCount);
        Assert.Null(completedState.LockedUntil);
        Assert.Null(completedState.LastErrorRedacted);
        Assert.Equal(1, await CountRows(
            "infrastructure.outbox_test_effects",
            "message_id",
            messageId));
        Assert.Equal(1, await CountRows(
            "infrastructure.processed_messages",
            "message_id",
            messageId));
    }

    [Fact]
    public async Task Event_without_registered_consumer_is_deferred_without_exhausting_retries()
    {
        Guid messageId;
        await using (var scope = Services.CreateAsyncScope())
        {
            var writer = scope.ServiceProvider.GetRequiredService<IOutboxWriter>();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            messageId = writer.Enqueue(
                "Tests.ConsumerNotRegistered.v1",
                new { sourceId = Guid.NewGuid() },
                _clock.UtcNow);
            await context.SaveChangesAsync();
        }

        var firstAttempt = await ProcessBatch();

        Assert.Equal(new OutboxBatchResult(1, 0, 1), firstAttempt);
        var deferred = await GetMessageState(messageId);
        Assert.Equal("Pending", deferred.Status);
        Assert.Equal(0, deferred.AttemptCount);
        Assert.Equal(_clock.UtcNow.AddMinutes(1), deferred.AvailableAt);
        Assert.Null(deferred.LockedUntil);
        Assert.Equal("NoHandlerRegistered", deferred.LastErrorRedacted);

        _clock.Advance(TimeSpan.FromMinutes(1));
        var secondAttempt = await ProcessBatch();
        Assert.Equal(new OutboxBatchResult(1, 0, 1), secondAttempt);
        Assert.Equal(0, (await GetMessageState(messageId)).AttemptCount);
    }

    [Fact]
    public async Task Expired_processing_lease_is_reclaimed_after_worker_crash()
    {
        var messageId = await EnqueueMessage();
        var firstLease = _clock.UtcNow.AddSeconds(30);

        await ExecuteNonQuery(
            """
            UPDATE infrastructure.outbox_messages
            SET status = 'Processing', attempt_count = 1, locked_until = @locked_until
            WHERE id = @id;
            """,
            new NpgsqlParameter("locked_until", firstLease),
            new NpgsqlParameter("id", messageId));

        Assert.Equal(0, (await ProcessBatch()).ClaimedCount);

        _clock.Advance(TimeSpan.FromSeconds(31));
        var recovered = await ProcessBatch();

        Assert.Equal(new OutboxBatchResult(1, 1, 0), recovered);
        var state = await GetMessageState(messageId);
        Assert.Equal("Completed", state.Status);
        Assert.Equal(2, state.AttemptCount);
        Assert.Equal(1, await CountRows(
            "infrastructure.outbox_test_effects",
            "message_id",
            messageId));
    }

    [Fact]
    public async Task Exhausted_message_is_retained_as_failed_without_sensitive_error_text()
    {
        _probe.FailuresRemaining = 2;
        var messageId = await EnqueueMessage();

        await ProcessBatch();
        _clock.Advance(TimeSpan.FromSeconds(10));
        var finalAttempt = await ProcessBatch();

        Assert.Equal(new OutboxBatchResult(1, 0, 1), finalAttempt);
        var state = await GetMessageState(messageId);
        Assert.Equal("Failed", state.Status);
        Assert.Equal(2, state.AttemptCount);
        Assert.Equal(nameof(SimulatedHandlerFailureException), state.LastErrorRedacted);
        Assert.DoesNotContain("sensitive", state.LastErrorRedacted, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, (await ProcessBatch()).ClaimedCount);
        Assert.Equal(0, await CountRows(
            "infrastructure.outbox_test_effects",
            "message_id",
            messageId));
    }

    [Fact]
    public async Task Concurrent_processors_claim_each_message_once()
    {
        _probe.Delay = TimeSpan.FromMilliseconds(100);

        for (var index = 0; index < 4; index++)
        {
            await EnqueueMessage();
        }

        var results = await Task.WhenAll(ProcessBatch(), ProcessBatch());

        Assert.Equal(4, results.Sum(result => result.ClaimedCount));
        Assert.Equal(4, results.Sum(result => result.CompletedCount));
        Assert.Equal(0, results.Sum(result => result.FailedCount));
        Assert.Equal(4, await CountRows("infrastructure.outbox_test_effects"));
        Assert.Equal(4, await CountRows("infrastructure.processed_messages"));
        Assert.Equal(4, await CountCompletedFirstAttemptMessages());
    }

    [Fact]
    public async Task Migration_enforces_outbox_checks_inbox_key_and_claim_index()
    {
        Assert.True(await IndexExists("ix_outbox_messages_status_available_at"));

        var checkViolation = await Assert.ThrowsAsync<PostgresException>(() => ExecuteNonQuery(
            """
            INSERT INTO infrastructure.outbox_messages
                (id, event_type, payload, occurred_at, available_at, status, attempt_count, locked_until)
            VALUES
                (@id, 'tests.invalid', '{}'::jsonb, @now, @now, 'Pending', -1, NULL);
            """,
            new NpgsqlParameter("id", Guid.NewGuid()),
            new NpgsqlParameter("now", _clock.UtcNow)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, checkViolation.SqlState);

        var messageId = Guid.NewGuid();
        await ExecuteNonQuery(
            """
            INSERT INTO infrastructure.processed_messages (consumer, message_id, processed_at)
            VALUES ('tests.constraint', @message_id, @processed_at);
            """,
            new NpgsqlParameter("message_id", messageId),
            new NpgsqlParameter("processed_at", _clock.UtcNow));

        var uniqueViolation = await Assert.ThrowsAsync<PostgresException>(() => ExecuteNonQuery(
            """
            INSERT INTO infrastructure.processed_messages (consumer, message_id, processed_at)
            VALUES ('tests.constraint', @message_id, @processed_at);
            """,
            new NpgsqlParameter("message_id", messageId),
            new NpgsqlParameter("processed_at", _clock.UtcNow)));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, uniqueViolation.SqlState);
    }

    private ServiceProvider Services => _serviceProvider
        ?? throw new InvalidOperationException("Test services have not been initialized.");

    private async Task<Guid> EnqueueMessage()
    {
        await using var scope = Services.CreateAsyncScope();
        var writer = scope.ServiceProvider.GetRequiredService<IOutboxWriter>();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var messageId = writer.Enqueue(
            EventType,
            new { subjectId = Guid.NewGuid() },
            _clock.UtcNow);
        await context.SaveChangesAsync();
        return messageId;
    }

    private async Task<OutboxBatchResult> ProcessBatch()
    {
        await using var scope = Services.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>();
        return await processor.ProcessBatchAsync(CancellationToken.None);
    }

    private async Task<MessageState> GetMessageState(Guid messageId)
    {
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT status, attempt_count, available_at, locked_until, last_error_redacted
            FROM infrastructure.outbox_messages
            WHERE id = @id;
            """;
        command.Parameters.AddWithValue("id", messageId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        return new MessageState(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.GetFieldValue<DateTimeOffset>(2),
            reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
            reader.IsDBNull(4) ? null : reader.GetString(4));
    }

    private async Task<long> CountRows(
        string qualifiedTable,
        string? idColumn = null,
        Guid? id = null)
    {
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = idColumn is null
            ? $"SELECT COUNT(*) FROM {qualifiedTable};"
            : $"SELECT COUNT(*) FROM {qualifiedTable} WHERE {idColumn} = @id;";

        if (idColumn is not null)
        {
            command.Parameters.AddWithValue("id", id!.Value);
        }

        return (long)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("COUNT returned null."));
    }

    private async Task<long> CountCompletedFirstAttemptMessages()
    {
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM infrastructure.outbox_messages
            WHERE status = 'Completed' AND attempt_count = 1;
            """;
        return (long)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("COUNT returned null."));
    }

    private async Task<bool> IndexExists(string indexName)
    {
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT EXISTS
            (
                SELECT 1
                FROM pg_indexes
                WHERE schemaname = 'infrastructure' AND indexname = @index_name
            );
            """;
        command.Parameters.AddWithValue("index_name", indexName);
        return (bool)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("EXISTS returned null."));
    }

    private async Task<int> ExecuteNonQuery(string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        return await command.ExecuteNonQueryAsync();
    }

    private sealed record MessageState(
        string Status,
        int AttemptCount,
        DateTimeOffset AvailableAt,
        DateTimeOffset? LockedUntil,
        string? LastErrorRedacted);

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = utcNow;

        internal void Advance(TimeSpan duration) => UtcNow = UtcNow.Add(duration);
    }

    private sealed class HandlerProbe
    {
        private int _failuresRemaining;

        internal int FailuresRemaining
        {
            set => _failuresRemaining = value;
        }

        internal TimeSpan Delay { get; set; }

        internal bool ConsumeFailure()
        {
            while (true)
            {
                var current = Volatile.Read(ref _failuresRemaining);

                if (current <= 0)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(
                    ref _failuresRemaining,
                    current - 1,
                    current) == current)
                {
                    return true;
                }
            }
        }
    }

    private sealed class ProbeOutboxMessageHandler(
        AppDbContext dbContext,
        HandlerProbe probe,
        IClock clock) : IOutboxMessageHandler
    {
        public string ConsumerName => OutboxProcessingTests.ConsumerName;

        public string EventType => OutboxProcessingTests.EventType;

        public async Task HandleAsync(
            OutboxMessageEnvelope message,
            CancellationToken cancellationToken)
        {
            if (probe.Delay > TimeSpan.Zero)
            {
                await Task.Delay(probe.Delay, cancellationToken);
            }

            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO infrastructure.outbox_test_effects (message_id, handled_at)
                VALUES ({message.Id}, {clock.UtcNow});
                """,
                cancellationToken);

            if (probe.ConsumeFailure())
            {
                throw new SimulatedHandlerFailureException("sensitive failure text");
            }
        }
    }

    private sealed class SimulatedHandlerFailureException(string message) : Exception(message);
}
