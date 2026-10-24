using ComicManagement.Catalog.Chapters;
using ComicManagement.Catalog.Comics;
using ComicManagement.Identity.Users;
using ComicManagement.Interaction.Comments;
using ComicManagement.Persistence;
using ComicManagement.Shared.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace ComicManagement.Persistence.IntegrationTests;

public sealed class CommentPersistenceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 18, 0, 0, TimeSpan.Zero);
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18.0-alpine3.22").WithDatabase("comments").WithUsername("postgres").WithPassword("postgres").Build();
    private ServiceProvider _services = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _services = new ServiceCollection().AddPersistence(_postgres.GetConnectionString()).BuildServiceProvider(true);
        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
    }
    public async Task DisposeAsync() { await _services.DisposeAsync(); await _postgres.DisposeAsync(); }

    [Fact]
    public async Task Root_and_one_reply_are_created_but_nested_or_cross_target_reply_is_rejected()
    {
        var (comicId, chapterId, userId) = await SeedAsync();
        await using var scope = _services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<CommentHandler>();
        var root = await handler.CreateAsync(userId, comicId, null, null, "Root");
        var reply = await handler.CreateAsync(userId, comicId, null, root.Comment!.Id, "Reply");
        Assert.Equal(CommentOutcome.Succeeded, reply.Outcome);
        Assert.Equal(CommentOutcome.InvalidParent, (await handler.CreateAsync(userId, comicId, null, reply.Comment!.Id, "Nested")).Outcome);
        Assert.Equal(CommentOutcome.InvalidParent, (await handler.CreateAsync(userId, null, chapterId, root.Comment.Id, "Cross")).Outcome);
    }

    [Fact]
    public async Task Owner_only_edit_delete_and_deleted_comments_leave_public_listing()
    {
        var (comicId, _, owner) = await SeedAsync();
        var other = await SeedUserAsync("other");
        await using var scope = _services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<CommentHandler>();
        var created = await handler.CreateAsync(owner, comicId, null, null, "Body");
        Assert.Equal(CommentOutcome.Forbidden, (await handler.EditAsync(created.Comment!.Id, other, "Hack", 1)).Outcome);
        var edited = await handler.EditAsync(created.Comment.Id, owner, "Edited", 1);
        Assert.Equal("Edited", edited.Comment!.Body);
        Assert.Equal(CommentOutcome.Succeeded, (await handler.DeleteAsync(created.Comment.Id, owner, 2)).Outcome);
        Assert.Empty((await handler.ListTargetAsync(CommentTargetTypes.Comic, comicId, 1)).Page!.Items);
    }

    [Fact]
    public async Task Admin_hide_restore_delete_is_audited_and_deleted_never_restores()
    {
        var (comicId, _, userId) = await SeedAsync();
        var adminId = await SeedUserAsync("admin");
        await using var scope = _services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<CommentHandler>();
        var created = await handler.CreateAsync(userId, comicId, null, null, "Body");
        var hidden = await handler.ModerateAsync(created.Comment!.Id, CommentModerationActions.Hide, 1, adminId, "trace-hide", "policy", default);
        Assert.Equal(CommentStatuses.Hidden, hidden.Comment!.Status);
        var restored = await handler.ModerateAsync(created.Comment.Id, CommentModerationActions.Restore, 2, adminId, "trace-restore", "appeal", default);
        var deleted = await handler.ModerateAsync(created.Comment.Id, CommentModerationActions.Delete, 3, adminId, "trace-delete", "policy", default);
        Assert.Equal(CommentOutcome.InvalidState, (await handler.ModerateAsync(created.Comment.Id, CommentModerationActions.Restore, 4, adminId, "trace", "no", default)).Outcome);
        Assert.Equal(CommentStatuses.Visible, restored.Comment!.Status);
        Assert.Equal(CommentStatuses.Deleted, deleted.Comment!.Status);
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(3, await context.AuditLogs.CountAsync(log => log.TargetId == created.Comment.Id));
    }

    [Fact]
    public async Task Database_rejects_invalid_target_status_body_and_parent_self()
    {
        var (comicId, _, userId) = await SeedAsync();
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        async Task AssertCheckAsync(string values)
        {
            await using var command = new NpgsqlCommand($"INSERT INTO interaction.comments(id,user_id,comic_id,chapter_id,parent_id,body,status,created_at,updated_at,version) VALUES ({values})", connection);
            var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        }
        var id = Guid.NewGuid();
        await AssertCheckAsync($"'{id}','{userId}',NULL,NULL,NULL,'body','Visible',now(),now(),1");
        await AssertCheckAsync($"'{Guid.NewGuid()}','{userId}','{comicId}',NULL,NULL,'   ','Visible',now(),now(),1");
        await AssertCheckAsync($"'{Guid.NewGuid()}','{userId}','{comicId}',NULL,NULL,'body','Bad',now(),now(),1");
        await AssertCheckAsync($"'{id}','{userId}','{comicId}',NULL,'{id}','body','Visible',now(),now(),1");

        async Task AssertForeignKeyAsync(string values)
        {
            await using var command = new NpgsqlCommand($"INSERT INTO interaction.comments(id,user_id,comic_id,chapter_id,parent_id,body,status,created_at,updated_at,version) VALUES ({values})", connection);
            var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
        }
        await AssertForeignKeyAsync($"'{Guid.NewGuid()}','{Guid.NewGuid()}','{comicId}',NULL,NULL,'body','Visible',now(),now(),1");
        await AssertForeignKeyAsync($"'{Guid.NewGuid()}','{userId}','{Guid.NewGuid()}',NULL,NULL,'body','Visible',now(),now(),1");
    }

    [Fact]
    public async Task Hidden_target_fails_closed_for_create_and_public_listing()
    {
        var (comicId, _, userId) = await SeedAsync();
        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE catalog.comics SET publication_status = {ComicPublicationStatuses.Hidden} WHERE id = {comicId}");
        var handler = scope.ServiceProvider.GetRequiredService<CommentHandler>();
        Assert.Equal(
            CommentOutcome.TargetUnavailable,
            (await handler.CreateAsync(userId, comicId, null, null, "Hidden")).Outcome);
        Assert.Equal(
            CommentOutcome.TargetUnavailable,
            (await handler.ListTargetAsync(CommentTargetTypes.Comic, comicId, 1)).Outcome);
    }

    [Fact]
    public async Task Create_waiting_on_comic_lock_revalidates_hidden_state_and_inserts_nothing()
    {
        var (comicId, _, userId) = await SeedAsync();
        await using var blocker = new NpgsqlConnection(_postgres.GetConnectionString());
        await blocker.OpenAsync();
        await using var blockerTransaction = await blocker.BeginTransactionAsync();
        await using (var hide = new NpgsqlCommand(
            "UPDATE catalog.comics SET publication_status = 'Hidden' WHERE id = @id RETURNING id",
            blocker,
            blockerTransaction))
        {
            hide.Parameters.AddWithValue("id", comicId);
            Assert.Equal(comicId, await hide.ExecuteScalarAsync());
        }

        await using var createScope = _services.CreateAsyncScope();
        var create = createScope.ServiceProvider.GetRequiredService<CommentHandler>()
            .CreateAsync(userId, comicId, null, null, "Must not survive hide");
        await Task.Delay(150);
        Assert.False(create.IsCompleted, "Create must contend on the authoritative Comic lock.");

        await blockerTransaction.CommitAsync();
        Assert.Equal(CommentOutcome.TargetUnavailable, (await create).Outcome);
        await using var verification = _services.CreateAsyncScope();
        Assert.False(await verification.ServiceProvider.GetRequiredService<AppDbContext>().Comments
            .AnyAsync(comment => comment.ComicId == comicId && comment.Body == "Must not survive hide"));
    }

    [Fact]
    public async Task Create_waiting_on_chapter_lock_revalidates_hidden_state_and_inserts_nothing()
    {
        var (comicId, chapterId, userId) = await SeedAsync();
        await using var blocker = new NpgsqlConnection(_postgres.GetConnectionString());
        await blocker.OpenAsync();
        await using var blockerTransaction = await blocker.BeginTransactionAsync();
        await using (var lockComic = new NpgsqlCommand(
            "SELECT id FROM catalog.comics WHERE id = @id FOR UPDATE",
            blocker,
            blockerTransaction))
        {
            lockComic.Parameters.AddWithValue("id", comicId);
            Assert.Equal(comicId, await lockComic.ExecuteScalarAsync());
        }
        await using (var hide = new NpgsqlCommand(
            "UPDATE catalog.chapters SET publication_status = 'Hidden' WHERE id = @id RETURNING id",
            blocker,
            blockerTransaction))
        {
            hide.Parameters.AddWithValue("id", chapterId);
            Assert.Equal(chapterId, await hide.ExecuteScalarAsync());
        }

        await using var createScope = _services.CreateAsyncScope();
        var create = createScope.ServiceProvider.GetRequiredService<CommentHandler>()
            .CreateAsync(userId, null, chapterId, null, "Must not survive chapter hide");
        await Task.Delay(150);
        Assert.False(create.IsCompleted, "Create must contend using Catalog's Comic-before-Chapter lock order.");

        await blockerTransaction.CommitAsync();
        Assert.Equal(CommentOutcome.TargetUnavailable, (await create).Outcome);
        await using var verification = _services.CreateAsyncScope();
        Assert.False(await verification.ServiceProvider.GetRequiredService<AppDbContext>().Comments
            .AnyAsync(comment => comment.ChapterId == chapterId));
    }

    [Fact]
    public async Task Create_waiting_on_ready_page_lock_revalidates_readiness_and_inserts_nothing()
    {
        var (_, chapterId, userId) = await SeedAsync();
        await using var blocker = new NpgsqlConnection(_postgres.GetConnectionString());
        await blocker.OpenAsync();
        await using var blockerTransaction = await blocker.BeginTransactionAsync();
        await using (var makeNotReady = new NpgsqlCommand(
            "UPDATE catalog.chapter_pages SET asset_status = 'Pending', width = 0, height = 0 WHERE chapter_id = @id RETURNING id",
            blocker,
            blockerTransaction))
        {
            makeNotReady.Parameters.AddWithValue("id", chapterId);
            Assert.IsType<Guid>(await makeNotReady.ExecuteScalarAsync());
        }

        await using var createScope = _services.CreateAsyncScope();
        var create = createScope.ServiceProvider.GetRequiredService<CommentHandler>()
            .CreateAsync(userId, null, chapterId, null, "Must not survive readiness loss");
        await Task.Delay(150);
        Assert.False(create.IsCompleted, "Create must contend on authoritative readiness rows.");

        await blockerTransaction.CommitAsync();
        Assert.Equal(CommentOutcome.TargetUnavailable, (await create).Outcome);
        await using var verification = _services.CreateAsyncScope();
        Assert.False(await verification.ServiceProvider.GetRequiredService<AppDbContext>().Comments
            .AnyAsync(comment => comment.ChapterId == chapterId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Owner_mutation_and_admin_moderation_contend_and_only_one_stale_version_wins(bool ownerDeletes)
    {
        var (comicId, _, ownerId) = await SeedAsync();
        var adminId = await SeedUserAsync("admin-race");
        Guid commentId;
        await using (var seedScope = _services.CreateAsyncScope())
        {
            var created = await seedScope.ServiceProvider.GetRequiredService<CommentHandler>()
                .CreateAsync(ownerId, comicId, null, null, "Race");
            commentId = created.Comment!.Id;
        }

        await using var blocker = new NpgsqlConnection(_postgres.GetConnectionString());
        await blocker.OpenAsync();
        await using var blockerTransaction = await blocker.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand(
            "SELECT id FROM interaction.comments WHERE id = @id FOR UPDATE",
            blocker,
            blockerTransaction))
        {
            command.Parameters.AddWithValue("id", commentId);
            Assert.Equal(commentId, await command.ExecuteScalarAsync());
        }

        await using var ownerScope = _services.CreateAsyncScope();
        await using var adminScope = _services.CreateAsyncScope();
        var ownerHandler = ownerScope.ServiceProvider.GetRequiredService<CommentHandler>();
        var adminHandler = adminScope.ServiceProvider.GetRequiredService<CommentHandler>();
        var ownerMutation = ownerDeletes
            ? ownerHandler.DeleteAsync(commentId, ownerId, 1)
            : ownerHandler.EditAsync(commentId, ownerId, "Owner edit", 1);
        var moderation = adminHandler.ModerateAsync(
            commentId, CommentModerationActions.Hide, 1, adminId, "race-trace", "race", default);
        await Task.Delay(150);
        Assert.False(ownerMutation.IsCompleted);
        Assert.False(moderation.IsCompleted);

        await blockerTransaction.CommitAsync();
        var outcomes = new[] { (await ownerMutation).Outcome, (await moderation).Outcome };
        Assert.Single(outcomes, outcome => outcome == CommentOutcome.Succeeded);
        Assert.Single(outcomes, outcome => outcome == CommentOutcome.ConcurrencyConflict);
    }

    [Fact]
    public async Task Comment_indexes_match_public_and_moderation_ordering()
    {
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT indexname FROM pg_indexes WHERE schemaname = 'interaction' AND tablename = 'comments'",
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        var indexes = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync()) indexes.Add(reader.GetString(0));
        Assert.Contains("ix_comments_created_id", indexes);
        Assert.Contains("ix_comments_status_created_id", indexes);
        Assert.DoesNotContain("ix_comments_status_updated_id", indexes);
    }

    [Fact]
    public async Task Paged_comment_reads_keep_count_and_items_in_one_repeatable_read_snapshot()
    {
        var (comicId, _, userId) = await SeedAsync();
        await using (var seedScope = _services.CreateAsyncScope())
            await seedScope.ServiceProvider.GetRequiredService<CommentHandler>()
                .CreateAsync(userId, comicId, null, null, "Before snapshot");

        await using var readScope = _services.CreateAsyncScope();
        var readContext = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var executor = readScope.ServiceProvider.GetRequiredService<IConsistentReadExecutor>();
        var snapshot = await executor.ExecuteAsync(async token =>
        {
            var total = await readContext.Comments.CountAsync(token);
            await using var writerScope = _services.CreateAsyncScope();
            var inserted = await writerScope.ServiceProvider.GetRequiredService<CommentHandler>()
                .CreateAsync(userId, comicId, null, null, "After snapshot", token);
            Assert.Equal(CommentOutcome.Succeeded, inserted.Outcome);
            var items = await readContext.Comments.AsNoTracking().OrderBy(comment => comment.Id).ToArrayAsync(token);
            return (total, items.Length);
        });

        Assert.Equal(1, snapshot.total);
        Assert.Equal(snapshot.total, snapshot.Length);
    }

    [Fact]
    public async Task Mutation_response_is_the_snapshot_written_by_that_transaction_not_a_later_mutation()
    {
        var (comicId, _, ownerId) = await SeedAsync();
        Guid commentId;
        await using (var seedScope = _services.CreateAsyncScope())
            commentId = (await seedScope.ServiceProvider.GetRequiredService<CommentHandler>()
                .CreateAsync(ownerId, comicId, null, null, "Initial")).Comment!.Id;

        await using (var connection = new NpgsqlConnection(_postgres.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                """
                CREATE OR REPLACE FUNCTION interaction.delay_comment_update() RETURNS trigger
                LANGUAGE plpgsql AS $$ BEGIN PERFORM pg_sleep(0.35); RETURN NEW; END $$;
                CREATE TRIGGER delay_comment_update BEFORE UPDATE ON interaction.comments
                FOR EACH ROW EXECUTE FUNCTION interaction.delay_comment_update();
                """,
                connection);
            await command.ExecuteNonQueryAsync();
        }

        await using var firstScope = _services.CreateAsyncScope();
        await using var secondScope = _services.CreateAsyncScope();
        var first = firstScope.ServiceProvider.GetRequiredService<CommentHandler>()
            .EditAsync(commentId, ownerId, "First mutation", 1);
        await Task.Delay(100);
        var second = secondScope.ServiceProvider.GetRequiredService<CommentHandler>()
            .EditAsync(commentId, ownerId, "Second mutation", 2);

        var firstResult = await first;
        var secondResult = await second;
        Assert.Equal(CommentOutcome.Succeeded, firstResult.Outcome);
        Assert.Equal(2, firstResult.Comment!.Version);
        Assert.Equal("First mutation", firstResult.Comment.Body);
        Assert.Equal(CommentOutcome.Succeeded, secondResult.Outcome);
        Assert.Equal(3, secondResult.Comment!.Version);
        Assert.Equal("Second mutation", secondResult.Comment.Body);
    }

    private async Task<(Guid ComicId, Guid ChapterId, Guid UserId)> SeedAsync()
    {
        var userId = await SeedUserAsync($"user-{Guid.NewGuid():N}");
        var comicId = Guid.NewGuid(); var chapterId = Guid.NewGuid();
        await using var scope = _services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var comic = Comic.Create(comicId, $"comments-{comicId:N}", "Comments", "", ComicSeriesStatuses.Ongoing, null, 0, false, Now);
        var chapter = Chapter.Create(chapterId, comicId, "1", 1, 0, null, Now);
        var page = ChapterPage.CreatePending(Guid.NewGuid(), chapterId, 1, $"comments/{chapterId:N}/1", $"sha256:{chapterId:N}{chapterId:N}", Now);
        page.MarkReady(100, 100); chapter.Publish(true, 1, Now.AddMinutes(1)); comic.Publish(true, 1, Now.AddMinutes(2));
        db.AddRange(comic, chapter, page); await db.SaveChangesAsync(); return (comicId, chapterId, userId);
    }

    private async Task<Guid> SeedUserAsync(string name)
    {
        var id = Guid.NewGuid(); await using var scope = _services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Users.Add(User.CreateFromGoogle(id, name, $"{name}@example.com", null, Now)); await db.SaveChangesAsync(); return id;
    }
}
