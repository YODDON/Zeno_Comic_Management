using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMissionProgressAndRewards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "ak_mission_revisions_id_mission_id",
                schema: "missions",
                table: "mission_revisions",
                columns: new[] { "id", "mission_id" });

            migrationBuilder.CreateTable(
                name: "daily_counters",
                schema: "missions",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    local_date = table.Column<DateOnly>(type: "date", nullable: false),
                    granted_total = table.Column<long>(type: "bigint", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_daily_counters", x => new { x.user_id, x.local_date });
                    table.CheckConstraint("ck_daily_counters_granted_total", "granted_total BETWEEN 0 AND 300");
                    table.CheckConstraint("ck_daily_counters_version", "version >= 1");
                    table.ForeignKey(
                        name: "fk_daily_counters_user",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "mission_activations",
                schema: "missions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    mission_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mission_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    activation_sequence = table.Column<long>(type: "bigint", nullable: false),
                    activated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deactivated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mission_activations", x => x.id);
                    table.CheckConstraint("ck_mission_activations_sequence", "activation_sequence >= 1");
                    table.CheckConstraint("ck_mission_activations_window", "deactivated_at IS NULL OR deactivated_at >= activated_at");
                    table.ForeignKey(
                        name: "fk_mission_activations_revision_owner",
                        columns: x => new { x.mission_revision_id, x.mission_definition_id },
                        principalSchema: "missions",
                        principalTable: "mission_revisions",
                        principalColumns: new[] { "id", "mission_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "mission_progress",
                schema: "missions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mission_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    period_key = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    progress_count = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mission_progress", x => x.id);
                    table.UniqueConstraint("ak_mission_progress_id_user_id", x => new { x.id, x.user_id });
                    table.CheckConstraint("ck_mission_progress_completion", "(status = 'InProgress' AND completed_at IS NULL) OR (status = 'Completed' AND completed_at IS NOT NULL)");
                    table.CheckConstraint("ck_mission_progress_count", "progress_count >= 0");
                    table.CheckConstraint("ck_mission_progress_status", "status IN ('InProgress','Completed')");
                    table.CheckConstraint("ck_mission_progress_version", "version >= 1");
                    table.ForeignKey(
                        name: "fk_mission_progress_revision",
                        column: x => x.mission_revision_id,
                        principalSchema: "missions",
                        principalTable: "mission_revisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_mission_progress_user",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "progress_actions",
                schema: "missions",
                columns: table => new
                {
                    mission_progress_id = table.Column<Guid>(type: "uuid", nullable: false),
                    dedup_key = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    source_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_progress_actions", x => new { x.mission_progress_id, x.dedup_key });
                    table.ForeignKey(
                        name: "fk_progress_actions_progress",
                        column: x => x.mission_progress_id,
                        principalSchema: "missions",
                        principalTable: "mission_progress",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "reward_grants",
                schema: "missions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    progress_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bonus_coins = table.Column<long>(type: "bigint", nullable: false),
                    outcome = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    local_date = table.Column<DateOnly>(type: "date", nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    wallet_operation_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reward_grants", x => x.id);
                    table.CheckConstraint("ck_reward_grants_bonus", "bonus_coins BETWEEN 1 AND 300");
                    table.CheckConstraint("ck_reward_grants_outcome", "outcome IN ('Granted','DeniedDailyCap')");
                    table.CheckConstraint("ck_reward_grants_wallet_operation", "(outcome = 'Granted' AND wallet_operation_id IS NOT NULL) OR (outcome = 'DeniedDailyCap' AND wallet_operation_id IS NULL)");
                    table.ForeignKey(
                        name: "fk_reward_grants_progress_owner",
                        columns: x => new { x.progress_id, x.user_id },
                        principalSchema: "missions",
                        principalTable: "mission_progress",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_reward_grants_user",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_reward_grants_wallet_operation_owner",
                        columns: x => new { x.wallet_operation_id, x.user_id },
                        principalSchema: "wallet",
                        principalTable: "wallet_operations",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_mission_activations_history",
                schema: "missions",
                table: "mission_activations",
                columns: new[] { "mission_definition_id", "activated_at" });

            migrationBuilder.CreateIndex(
                name: "ix_mission_activations_revision_owner",
                schema: "missions",
                table: "mission_activations",
                columns: new[] { "mission_revision_id", "mission_definition_id" });

            migrationBuilder.CreateIndex(
                name: "ux_mission_activations_mission_sequence",
                schema: "missions",
                table: "mission_activations",
                columns: new[] { "mission_definition_id", "activation_sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_mission_activations_open",
                schema: "missions",
                table: "mission_activations",
                column: "mission_definition_id",
                unique: true,
                filter: "deactivated_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_mission_progress_revision",
                schema: "missions",
                table: "mission_progress",
                column: "mission_revision_id");

            migrationBuilder.CreateIndex(
                name: "ix_mission_progress_user_status",
                schema: "missions",
                table: "mission_progress",
                columns: new[] { "user_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_mission_progress_user_revision_period",
                schema: "missions",
                table: "mission_progress",
                columns: new[] { "user_id", "mission_revision_id", "period_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_progress_actions_source_event",
                schema: "missions",
                table: "progress_actions",
                column: "source_event_id");

            migrationBuilder.CreateIndex(
                name: "ix_reward_grants_progress_owner",
                schema: "missions",
                table: "reward_grants",
                columns: new[] { "progress_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_reward_grants_user_decided_id",
                schema: "missions",
                table: "reward_grants",
                columns: new[] { "user_id", "decided_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_reward_grants_wallet_operation_owner",
                schema: "missions",
                table: "reward_grants",
                columns: new[] { "wallet_operation_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ux_reward_grants_progress",
                schema: "missions",
                table: "reward_grants",
                column: "progress_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_reward_grants_wallet_operation",
                schema: "missions",
                table: "reward_grants",
                column: "wallet_operation_id",
                unique: true,
                filter: "wallet_operation_id IS NOT NULL");

            migrationBuilder.Sql(
                """
                DO $migration$
                BEGIN
                    IF EXISTS
                    (
                        SELECT 1
                        FROM missions.mission_definitions definition
                        LEFT JOIN LATERAL
                        (
                            SELECT audit.action, audit.redacted_changes
                            FROM administration.audit_logs audit
                            WHERE audit.target_type = 'MissionDefinition'
                              AND audit.target_id = definition.id
                              AND audit.action IN
                                  ('Missions.RevisionActivated', 'Missions.DefinitionDeactivated')
                            ORDER BY audit.occurred_at DESC, audit.id DESC
                            LIMIT 1
                        ) latest ON TRUE
                        WHERE definition.status = 'Active'
                          AND
                          (
                              latest.action IS DISTINCT FROM 'Missions.RevisionActivated'
                              OR NULLIF(latest.redacted_changes ->> 'ActiveRevisionId', '')::uuid
                                  IS DISTINCT FROM definition.active_revision_id
                          )
                    ) THEN
                        RAISE EXCEPTION
                            'M02 activation-history precondition failed: an active Mission lacks matching authoritative activation audit evidence.'
                            USING ERRCODE = '23514';
                    END IF;

                    IF EXISTS
                    (
                        SELECT 1
                        FROM administration.audit_logs audit
                        LEFT JOIN missions.mission_revisions revision
                          ON revision.id =
                             NULLIF(audit.redacted_changes ->> 'ActiveRevisionId', '')::uuid
                         AND revision.mission_id = audit.target_id
                        WHERE audit.target_type = 'MissionDefinition'
                          AND audit.action = 'Missions.RevisionActivated'
                          AND revision.id IS NULL
                    ) THEN
                        RAISE EXCEPTION
                            'M02 activation-history precondition failed: activation audit references an invalid revision.'
                            USING ERRCODE = '23514';
                    END IF;
                END;
                $migration$;

                WITH ordered_events AS
                (
                    SELECT
                        audit.id,
                        audit.target_id AS mission_definition_id,
                        audit.action,
                        NULLIF(audit.redacted_changes ->> 'ActiveRevisionId', '')::uuid
                            AS mission_revision_id,
                        audit.occurred_at,
                        lead(audit.occurred_at) OVER
                        (
                            PARTITION BY audit.target_id
                            ORDER BY audit.occurred_at, audit.id
                        ) AS next_event_at,
                        count(*) FILTER
                        (
                            WHERE audit.action = 'Missions.RevisionActivated'
                        ) OVER
                        (
                            PARTITION BY audit.target_id
                            ORDER BY audit.occurred_at, audit.id
                            ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
                        ) AS activation_sequence
                    FROM administration.audit_logs audit
                    WHERE audit.target_type = 'MissionDefinition'
                      AND audit.action IN
                          ('Missions.RevisionActivated', 'Missions.DefinitionDeactivated')
                )
                INSERT INTO missions.mission_activations
                    (id, mission_definition_id, mission_revision_id,
                     activation_sequence, activated_at, deactivated_at)
                SELECT
                    gen_random_uuid(),
                    event.mission_definition_id,
                    event.mission_revision_id,
                    event.activation_sequence,
                    event.occurred_at,
                    event.next_event_at
                FROM ordered_events event
                WHERE event.action = 'Missions.RevisionActivated';

                CREATE OR REPLACE FUNCTION missions.guard_mission_activation_history()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'Mission activation history cannot be deleted.';
                    END IF;

                    IF TG_OP = 'UPDATE' AND
                       NOT
                       (
                           OLD.id = NEW.id
                           AND OLD.mission_definition_id = NEW.mission_definition_id
                           AND OLD.mission_revision_id = NEW.mission_revision_id
                           AND OLD.activation_sequence = NEW.activation_sequence
                           AND OLD.activated_at = NEW.activated_at
                           AND OLD.deactivated_at IS NULL
                           AND NEW.deactivated_at IS NOT NULL
                           AND NEW.deactivated_at >= NEW.activated_at
                       ) THEN
                        RAISE EXCEPTION
                            'Mission activation history is immutable except for closing an open interval.';
                    END IF;

                    IF EXISTS
                    (
                        SELECT 1
                        FROM missions.mission_activations existing
                        WHERE existing.mission_definition_id = NEW.mission_definition_id
                          AND existing.id <> NEW.id
                          AND existing.activated_at <
                              coalesce(NEW.deactivated_at, 'infinity'::timestamptz)
                          AND NEW.activated_at <
                              coalesce(existing.deactivated_at, 'infinity'::timestamptz)
                    ) THEN
                        RAISE EXCEPTION 'Mission activation intervals cannot overlap.';
                    END IF;

                    RETURN NEW;
                END;
                $function$;

                CREATE TRIGGER mission_activations_history_guard
                BEFORE INSERT OR UPDATE OR DELETE ON missions.mission_activations
                FOR EACH ROW EXECUTE FUNCTION missions.guard_mission_activation_history();

                CREATE OR REPLACE FUNCTION missions.guard_activated_mission_policy()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    IF (OLD.action_type IS DISTINCT FROM NEW.action_type OR
                        OLD.cycle_type IS DISTINCT FROM NEW.cycle_type) AND
                       EXISTS
                       (
                           SELECT 1
                           FROM missions.mission_activations activation
                           WHERE activation.mission_definition_id = OLD.id
                       ) THEN
                        RAISE EXCEPTION
                            'An activated Mission action/cycle policy is immutable.';
                    END IF;
                    RETURN NEW;
                END;
                $function$;

                CREATE TRIGGER mission_definitions_activated_policy_guard
                BEFORE UPDATE OF action_type, cycle_type
                ON missions.mission_definitions
                FOR EACH ROW EXECUTE FUNCTION missions.guard_activated_mission_policy();

                CREATE OR REPLACE FUNCTION missions.validate_reward_grant_integrity(
                    checked_operation_id uuid)
                RETURNS void
                LANGUAGE plpgsql
                AS $function$
                DECLARE
                    reward missions.reward_grants%ROWTYPE;
                    operation wallet.wallet_operations%ROWTYPE;
                    entry_count integer;
                    entry_total bigint;
                BEGIN
                    SELECT * INTO reward
                    FROM missions.reward_grants
                    WHERE wallet_operation_id = checked_operation_id;
                    IF NOT FOUND THEN
                        RETURN;
                    END IF;

                    SELECT * INTO operation
                    FROM wallet.wallet_operations
                    WHERE id = checked_operation_id;
                    SELECT count(*)::integer, coalesce(sum(delta_coins), 0)::bigint
                    INTO entry_count, entry_total
                    FROM wallet.wallet_entries
                    WHERE operation_id = checked_operation_id
                      AND user_id = reward.user_id
                      AND coin_type = 'Bonus'
                      AND bucket = 'Available'
                      AND delta_coins > 0;

                    IF operation.id IS NULL
                       OR operation.user_id <> reward.user_id
                       OR operation.operation_type <> 'MissionReward'
                       OR operation.source_type <> 'RewardGrant'
                       OR operation.source_id <> reward.id
                       OR entry_count <> 1
                       OR entry_total <> reward.bonus_coins
                       OR EXISTS
                       (
                           SELECT 1 FROM wallet.wallet_entries invalid
                           WHERE invalid.operation_id = checked_operation_id
                             AND
                             (
                                 invalid.user_id <> reward.user_id
                                 OR invalid.coin_type <> 'Bonus'
                                 OR invalid.bucket <> 'Available'
                                 OR invalid.delta_coins <= 0
                             )
                       ) THEN
                        RAISE EXCEPTION
                            'Mission reward Wallet operation or ledger is inconsistent.'
                            USING ERRCODE = '23514';
                    END IF;
                END;
                $function$;

                CREATE OR REPLACE FUNCTION missions.check_reward_grant_integrity()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    PERFORM missions.validate_reward_grant_integrity(
                        CASE
                            WHEN TG_TABLE_SCHEMA = 'missions' THEN
                                (to_jsonb(NEW) ->> 'wallet_operation_id')::uuid
                            ELSE
                                (to_jsonb(NEW) ->> 'operation_id')::uuid
                        END);
                    RETURN NEW;
                END;
                $function$;

                CREATE CONSTRAINT TRIGGER reward_grants_financial_integrity
                AFTER INSERT OR UPDATE ON missions.reward_grants
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW
                WHEN (NEW.wallet_operation_id IS NOT NULL)
                EXECUTE FUNCTION missions.check_reward_grant_integrity();

                CREATE CONSTRAINT TRIGGER mission_reward_entries_financial_integrity
                AFTER INSERT OR UPDATE ON wallet.wallet_entries
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW
                EXECUTE FUNCTION missions.check_reward_grant_integrity();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS mission_reward_entries_financial_integrity
                    ON wallet.wallet_entries;
                DROP TRIGGER IF EXISTS reward_grants_financial_integrity
                    ON missions.reward_grants;
                DROP FUNCTION IF EXISTS missions.check_reward_grant_integrity();
                DROP FUNCTION IF EXISTS missions.validate_reward_grant_integrity(uuid);
                DROP TRIGGER IF EXISTS mission_definitions_activated_policy_guard
                    ON missions.mission_definitions;
                DROP FUNCTION IF EXISTS missions.guard_activated_mission_policy();
                DROP TRIGGER IF EXISTS mission_activations_history_guard
                    ON missions.mission_activations;
                DROP FUNCTION IF EXISTS missions.guard_mission_activation_history();
                """);

            migrationBuilder.DropTable(
                name: "daily_counters",
                schema: "missions");

            migrationBuilder.DropTable(
                name: "mission_activations",
                schema: "missions");

            migrationBuilder.DropTable(
                name: "progress_actions",
                schema: "missions");

            migrationBuilder.DropTable(
                name: "reward_grants",
                schema: "missions");

            migrationBuilder.DropTable(
                name: "mission_progress",
                schema: "missions");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_mission_revisions_id_mission_id",
                schema: "missions",
                table: "mission_revisions");
        }
    }
}
