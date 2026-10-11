using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicManagement.Persistence.Migrations;

public partial class InitialSchemas : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(name: DatabaseSchemas.Infrastructure);
        migrationBuilder.EnsureSchema(name: DatabaseSchemas.Identity);
        migrationBuilder.EnsureSchema(name: DatabaseSchemas.Catalog);
        migrationBuilder.EnsureSchema(name: DatabaseSchemas.Reading);
        migrationBuilder.EnsureSchema(name: DatabaseSchemas.Interaction);
        migrationBuilder.EnsureSchema(name: DatabaseSchemas.Billing);
        migrationBuilder.EnsureSchema(name: DatabaseSchemas.Wallet);
        migrationBuilder.EnsureSchema(name: DatabaseSchemas.Membership);
        migrationBuilder.EnsureSchema(name: DatabaseSchemas.Missions);
        migrationBuilder.EnsureSchema(name: DatabaseSchemas.Administration);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropSchema(name: DatabaseSchemas.Administration);
        migrationBuilder.DropSchema(name: DatabaseSchemas.Missions);
        migrationBuilder.DropSchema(name: DatabaseSchemas.Membership);
        migrationBuilder.DropSchema(name: DatabaseSchemas.Wallet);
        migrationBuilder.DropSchema(name: DatabaseSchemas.Billing);
        migrationBuilder.DropSchema(name: DatabaseSchemas.Interaction);
        migrationBuilder.DropSchema(name: DatabaseSchemas.Reading);
        migrationBuilder.DropSchema(name: DatabaseSchemas.Catalog);
        migrationBuilder.DropSchema(name: DatabaseSchemas.Identity);

        // The infrastructure schema owns EF's history table and must remain so EF can
        // record this rollback. It is reused when the migration is applied again.
    }
}
