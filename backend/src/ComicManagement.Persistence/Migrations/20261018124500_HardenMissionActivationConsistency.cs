using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicManagement.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261018124500_HardenMissionActivationConsistency")]
public partial class HardenMissionActivationConsistency : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DO $migration$
            BEGIN
                IF EXISTS
                (
                    SELECT 1
                    FROM missions.mission_definitions definition
                    LEFT JOIN missions.mission_revisions revision
                        ON revision.id = definition.active_revision_id
                    WHERE
                        (definition.status = 'Active') IS DISTINCT FROM
                            (definition.active_revision_id IS NOT NULL)
                        OR
                        (
                            definition.active_revision_id IS NOT NULL
                            AND
                            (
                                revision.id IS NULL
                                OR revision.mission_id <> definition.id
                                OR
                                (
                                    definition.action_type = 'DailyLogin'
                                    AND revision.repeat_policy <> 'OncePerVietnamDate'
                                )
                                OR
                                (
                                    definition.action_type <> 'DailyLogin'
                                    AND revision.repeat_policy <> 'UniqueSubjectPerPeriod'
                                )
                            )
                        )
                ) THEN
                    RAISE EXCEPTION
                        'Mission activation hardening precondition failed: existing active revision state is inconsistent.'
                        USING ERRCODE = '23514';
                END IF;
            END;
            $migration$;

            DROP TRIGGER IF EXISTS trg_mission_definition_active_revision_ownership
                ON missions.mission_definitions;

            CREATE TRIGGER trg_mission_definition_active_revision_ownership
            BEFORE INSERT OR UPDATE OF active_revision_id, action_type
            ON missions.mission_definitions
            FOR EACH ROW
            EXECUTE FUNCTION missions.enforce_active_revision_ownership();
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DROP TRIGGER IF EXISTS trg_mission_definition_active_revision_ownership
                ON missions.mission_definitions;

            CREATE TRIGGER trg_mission_definition_active_revision_ownership
            BEFORE INSERT OR UPDATE OF active_revision_id
            ON missions.mission_definitions
            FOR EACH ROW
            EXECUTE FUNCTION missions.enforce_active_revision_ownership();
            """);
    }
}
