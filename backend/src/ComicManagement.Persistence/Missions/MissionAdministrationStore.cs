using System.Data;
using System.Text.Json;
using ComicManagement.Administration.Auditing;
using ComicManagement.Missions.Administration;
using ComicManagement.Missions.Definitions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ComicManagement.Persistence;

internal sealed class MissionAdministrationStore(
    AppDbContext dbContext,
    IMissionActivationCriticalSectionObserver criticalSectionObserver) :
    IMissionAdministrationStore
{
    private const string DefinitionCodeConstraint = "ux_mission_definitions_code";

    public async Task<MissionAdministrationResult> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var definitions = await dbContext.MissionDefinitions
            .AsNoTracking()
            .OrderBy(definition => definition.Code)
            .ToArrayAsync(cancellationToken);
        var definitionIds = definitions.Select(definition => definition.Id).ToArray();
        var revisions = await dbContext.MissionRevisions
            .AsNoTracking()
            .Where(revision => definitionIds.Contains(revision.MissionId))
            .OrderBy(revision => revision.MissionId)
            .ThenBy(revision => revision.RevisionNumber)
            .ToArrayAsync(cancellationToken);

        return new MissionAdministrationResult(
            MissionAdministrationOutcome.Succeeded,
            Definitions: definitions
                .Select(definition => ToState(
                    definition,
                    revisions.Where(revision => revision.MissionId == definition.Id)))
                .ToArray());
    }

    public async Task<MissionAdministrationResult> GetAsync(
        Guid missionId,
        CancellationToken cancellationToken = default)
    {
        var definition = await dbContext.MissionDefinitions
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == missionId, cancellationToken);
        if (definition is null)
        {
            return NotFound();
        }

        var revisions = await dbContext.MissionRevisions
            .AsNoTracking()
            .Where(revision => revision.MissionId == missionId)
            .OrderBy(revision => revision.RevisionNumber)
            .ToArrayAsync(cancellationToken);
        return Succeeded(ToState(definition, revisions));
    }

    public async Task<MissionAdministrationResult> CreateAsync(
        CreateMissionDefinitionStoreRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        var serverNow = await GetServerTimeAsync(cancellationToken);
        var definition = MissionDefinition.Create(
            request.DefinitionId,
            request.Code,
            request.Name,
            request.Description,
            request.ActionType,
            request.CycleType,
            serverNow);
        dbContext.MissionDefinitions.Add(definition);
        AddAudit(
            request.ActorId,
            "Missions.DefinitionCreated",
            definition.Id,
            request.CorrelationId,
            serverNow,
            new
            {
                definition.Code,
                definition.Name,
                definition.ActionType,
                definition.CycleType,
                definition.Version,
            });

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Succeeded(ToState(definition, []));
        }
        catch (DbUpdateException exception) when (
            exception.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: DefinitionCodeConstraint,
            })
        {
            dbContext.ChangeTracker.Clear();
            return new MissionAdministrationResult(MissionAdministrationOutcome.CodeConflict);
        }
    }

    public async Task<MissionAdministrationResult> AddRevisionAsync(
        AddMissionRevisionStoreRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        var definition = await LockDefinitionAsync(request.MissionId, cancellationToken);
        if (definition is null)
        {
            return NotFound();
        }

        var expectedRepeatPolicy = definition.ActionType == MissionActionTypes.DailyLogin
            ? MissionRepeatPolicies.OncePerVietnamDate
            : MissionRepeatPolicies.UniqueSubjectPerPeriod;
        if (!string.Equals(
                request.RepeatPolicy,
                expectedRepeatPolicy,
                StringComparison.Ordinal))
        {
            return StateConflict();
        }

        var nextRevisionNumber = (await dbContext.MissionRevisions
            .Where(revision => revision.MissionId == definition.Id)
            .MaxAsync(
                revision => (int?)revision.RevisionNumber,
                cancellationToken) ?? 0) + 1;
        var revision = MissionRevision.Create(
            request.RevisionId,
            definition.Id,
            nextRevisionNumber,
            request.TargetCount,
            request.RewardBonusCoins,
            request.RepeatPolicy,
            request.EffectiveFrom,
            request.EffectiveUntil,
            request.ConfigurationSnapshot);
        var serverNow = await GetServerTimeAsync(cancellationToken);
        dbContext.MissionRevisions.Add(revision);
        AddAudit(
            request.ActorId,
            "Missions.RevisionCreated",
            definition.Id,
            request.CorrelationId,
            serverNow,
            new
            {
                RevisionId = revision.Id,
                revision.RevisionNumber,
                revision.TargetCount,
                revision.RewardBonusCoins,
                revision.RepeatPolicy,
                revision.EffectiveFrom,
                revision.EffectiveUntil,
            });
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new MissionAdministrationResult(
            MissionAdministrationOutcome.Succeeded,
            Revision: ToState(revision));
    }

    public async Task<MissionAdministrationResult> ChangeActivationAsync(
        ChangeMissionActivationStoreRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        var definition = await LockDefinitionAsync(request.MissionId, cancellationToken);
        if (definition is null)
        {
            return NotFound();
        }

        var previouslyActiveRevisionId = definition.ActiveRevisionId;
        await criticalSectionObserver.DefinitionLockedAsync(
            definition.Id,
            cancellationToken);
        MissionRevision? revision = null;
        try
        {
            if (request.RevisionId is { } revisionId)
            {
                revision = await dbContext.MissionRevisions
                    .AsNoTracking()
                    .SingleOrDefaultAsync(candidate => candidate.Id == revisionId, cancellationToken);
                if (revision is null || revision.MissionId != definition.Id)
                {
                    return StateConflict();
                }

                definition.Activate(revision, request.ExpectedVersion);
            }
            else
            {
                definition.Deactivate(request.ExpectedVersion);
            }
        }
        catch (MissionDefinitionConcurrencyException)
        {
            return new MissionAdministrationResult(
                MissionAdministrationOutcome.ConcurrencyConflict);
        }
        catch (ArgumentException)
        {
            return StateConflict();
        }

        var serverNow = await GetServerTimeAsync(cancellationToken);
        var openActivation = await dbContext.MissionActivations
            .FromSqlInterpolated($"""
                SELECT *
                FROM missions.mission_activations
                WHERE mission_definition_id = {definition.Id}
                  AND deactivated_at IS NULL
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken);
        if ((previouslyActiveRevisionId is null) != (openActivation is null) ||
            openActivation is not null &&
            openActivation.MissionRevisionId != previouslyActiveRevisionId)
        {
            throw new InvalidOperationException(
                "Mission activation history does not match the current definition state.");
        }

        openActivation?.Close(serverNow);
        if (openActivation is not null && revision is not null)
        {
            // Release the unique open-activation slot before inserting its successor.
            // Both saves remain inside this transaction and are therefore atomic.
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        if (revision is not null)
        {
            var nextSequence = (await dbContext.MissionActivations
                .Where(item => item.MissionDefinitionId == definition.Id)
                .MaxAsync(item => (long?)item.ActivationSequence, cancellationToken) ?? 0) + 1;
            dbContext.MissionActivations.Add(MissionActivation.Create(
                Guid.NewGuid(),
                definition.Id,
                revision.Id,
                nextSequence,
                serverNow));
        }

        AddAudit(
            request.ActorId,
            request.RevisionId is null
                ? "Missions.DefinitionDeactivated"
                : "Missions.RevisionActivated",
            definition.Id,
            request.CorrelationId,
            serverNow,
            new
            {
                definition.Status,
                definition.ActiveRevisionId,
                definition.Version,
            });
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var revisions = await dbContext.MissionRevisions
            .AsNoTracking()
            .Where(candidate => candidate.MissionId == definition.Id)
            .OrderBy(candidate => candidate.RevisionNumber)
            .ToArrayAsync(cancellationToken);
        return Succeeded(ToState(definition, revisions));
    }

    private Task<MissionDefinition?> LockDefinitionAsync(
        Guid missionId,
        CancellationToken cancellationToken) => dbContext.MissionDefinitions
        .FromSqlInterpolated($"""
            SELECT *
            FROM missions.mission_definitions
            WHERE id = {missionId}
            FOR UPDATE
            """)
        .SingleOrDefaultAsync(cancellationToken);

    private void AddAudit(
        Guid actorId,
        string action,
        Guid targetId,
        string correlationId,
        DateTimeOffset occurredAt,
        object changes) => dbContext.AuditLogs.Add(AuditLog.Create(
            Guid.NewGuid(),
            actorId,
            action,
            "MissionDefinition",
            targetId,
            correlationId,
            occurredAt,
            JsonSerializer.Serialize(changes)));

    private Task<DateTimeOffset> GetServerTimeAsync(CancellationToken cancellationToken) =>
        dbContext.Database
            .SqlQueryRaw<DateTimeOffset>(
                "SELECT clock_timestamp() AS \"Value\"")
            .SingleAsync(cancellationToken);

    private static MissionDefinitionState ToState(
        MissionDefinition definition,
        IEnumerable<MissionRevision> revisions) => new(
        definition.Id,
        definition.Code,
        definition.Name,
        definition.Description,
        definition.ActionType,
        definition.CycleType,
        definition.Status,
        definition.ActiveRevisionId,
        definition.CreatedAt,
        definition.Version,
        revisions.Select(ToState).ToArray());

    private static MissionRevisionState ToState(MissionRevision revision) => new(
        revision.Id,
        revision.MissionId,
        revision.RevisionNumber,
        revision.TargetCount,
        revision.RewardBonusCoins,
        revision.RepeatPolicy,
        revision.EffectiveFrom,
        revision.EffectiveUntil,
        ParseJson(revision.ConfigurationSnapshot));

    private static JsonElement ParseJson(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private static MissionAdministrationResult Succeeded(MissionDefinitionState definition) =>
        new(MissionAdministrationOutcome.Succeeded, Definition: definition);

    private static MissionAdministrationResult NotFound() =>
        new(MissionAdministrationOutcome.NotFound);

    private static MissionAdministrationResult StateConflict() =>
        new(MissionAdministrationOutcome.StateConflict);
}
