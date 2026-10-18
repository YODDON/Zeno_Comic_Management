using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMissionDefinitionsAndActionReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "missions");

            migrationBuilder.CreateTable(
                name: "action_receipts",
                schema: "missions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_event_type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    occurred_local_date = table.Column<DateOnly>(type: "date", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processing_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_action_receipts", x => x.id);
                    table.CheckConstraint("ck_action_receipts_processing_status", "processing_status IN ('Pending','Processed')");
                    table.ForeignKey(
                        name: "fk_action_receipts_user",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "mission_definitions",
                schema: "missions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    action_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    cycle_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    active_revision_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mission_definitions", x => x.id);
                    table.CheckConstraint("ck_mission_definitions_action_type", "action_type IN ('DailyLogin','ChapterReadQualified','ComicFollowed','ChapterUnlocked','WalletTopUpCompleted')");
                    table.CheckConstraint("ck_mission_definitions_active_revision", "(status = 'Draft' AND active_revision_id IS NULL) OR (status = 'Active' AND active_revision_id IS NOT NULL)");
                    table.CheckConstraint("ck_mission_definitions_cycle_type", "cycle_type IN ('Fixed','Daily','Weekly','Event')");
                    table.CheckConstraint("ck_mission_definitions_status", "status IN ('Draft','Active')");
                    table.CheckConstraint("ck_mission_definitions_version", "version >= 1");
                });

            migrationBuilder.CreateTable(
                name: "mission_revisions",
                schema: "missions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    mission_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision_number = table.Column<int>(type: "integer", nullable: false),
                    target_count = table.Column<int>(type: "integer", nullable: false),
                    reward_bonus_coins = table.Column<long>(type: "bigint", nullable: false),
                    repeat_policy = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    effective_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    effective_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    configuration_snapshot = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mission_revisions", x => x.id);
                    table.CheckConstraint("ck_mission_revisions_configuration_object", "jsonb_typeof(configuration_snapshot) = 'object'");
                    table.CheckConstraint("ck_mission_revisions_effective_window", "effective_until IS NULL OR effective_until > effective_from");
                    table.CheckConstraint("ck_mission_revisions_repeat_policy", "repeat_policy IN ('OncePerVietnamDate','UniqueSubjectPerPeriod')");
                    table.CheckConstraint("ck_mission_revisions_revision_number", "revision_number > 0");
                    table.CheckConstraint("ck_mission_revisions_reward", "reward_bonus_coins BETWEEN 1 AND 300");
                    table.CheckConstraint("ck_mission_revisions_target_count", "target_count > 0");
                    table.ForeignKey(
                        name: "fk_mission_revisions_definition",
                        column: x => x.mission_id,
                        principalSchema: "missions",
                        principalTable: "mission_definitions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_action_receipts_user_local_date_status",
                schema: "missions",
                table: "action_receipts",
                columns: new[] { "user_id", "occurred_local_date", "processing_status" });

            migrationBuilder.CreateIndex(
                name: "ux_action_receipts_source_event",
                schema: "missions",
                table: "action_receipts",
                columns: new[] { "source_event_id", "source_event_type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_mission_definitions_active_revision_id",
                schema: "missions",
                table: "mission_definitions",
                column: "active_revision_id");

            migrationBuilder.CreateIndex(
                name: "ux_mission_definitions_code",
                schema: "missions",
                table: "mission_definitions",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_mission_revisions_effective_window",
                schema: "missions",
                table: "mission_revisions",
                columns: new[] { "mission_id", "effective_from", "effective_until" });

            migrationBuilder.CreateIndex(
                name: "ux_mission_revisions_mission_number",
                schema: "missions",
                table: "mission_revisions",
                columns: new[] { "mission_id", "revision_number" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_mission_definitions_active_revision",
                schema: "missions",
                table: "mission_definitions",
                column: "active_revision_id",
                principalSchema: "missions",
                principalTable: "mission_revisions",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql(
                """
                CREATE FUNCTION missions.enforce_active_revision_ownership()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    IF NEW.active_revision_id IS NOT NULL AND NOT EXISTS
                    (
                        SELECT 1
                        FROM missions.mission_revisions revision
                        WHERE revision.id = NEW.active_revision_id
                          AND revision.mission_id = NEW.id
                    ) THEN
                        RAISE EXCEPTION 'Active revision must belong to its mission definition.'
                            USING ERRCODE = '23514';
                    END IF;
                    IF NEW.active_revision_id IS NOT NULL AND EXISTS
                    (
                        SELECT 1
                        FROM missions.mission_revisions revision
                        WHERE revision.id = NEW.active_revision_id
                          AND
                          (
                              (NEW.action_type = 'DailyLogin' AND
                               revision.repeat_policy <> 'OncePerVietnamDate')
                              OR
                              (NEW.action_type <> 'DailyLogin' AND
                               revision.repeat_policy <> 'UniqueSubjectPerPeriod')
                          )
                    ) THEN
                        RAISE EXCEPTION 'Active revision repeat policy does not match action semantics.'
                            USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END;
                $function$;

                CREATE TRIGGER trg_mission_definition_active_revision_ownership
                BEFORE INSERT OR UPDATE OF active_revision_id
                ON missions.mission_definitions
                FOR EACH ROW
                EXECUTE FUNCTION missions.enforce_active_revision_ownership();

                CREATE FUNCTION missions.prevent_mission_revision_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    RAISE EXCEPTION 'Mission revisions are append-only.'
                        USING ERRCODE = '55000';
                END;
                $function$;

                CREATE TRIGGER trg_mission_revisions_append_only
                BEFORE UPDATE OR DELETE
                ON missions.mission_revisions
                FOR EACH ROW
                EXECUTE FUNCTION missions.prevent_mission_revision_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS trg_mission_definition_active_revision_ownership
                    ON missions.mission_definitions;
                DROP FUNCTION IF EXISTS missions.enforce_active_revision_ownership();
                DROP TRIGGER IF EXISTS trg_mission_revisions_append_only
                    ON missions.mission_revisions;
                DROP FUNCTION IF EXISTS missions.prevent_mission_revision_mutation();
                """);

            migrationBuilder.DropForeignKey(
                name: "fk_mission_definitions_active_revision",
                schema: "missions",
                table: "mission_definitions");

            migrationBuilder.DropTable(
                name: "action_receipts",
                schema: "missions");

            migrationBuilder.DropTable(
                name: "mission_revisions",
                schema: "missions");

            migrationBuilder.DropTable(
                name: "mission_definitions",
                schema: "missions");
        }
    }
}
