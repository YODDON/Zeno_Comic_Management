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
