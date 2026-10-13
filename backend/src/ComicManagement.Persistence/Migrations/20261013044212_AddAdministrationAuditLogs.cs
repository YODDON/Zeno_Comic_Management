using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAdministrationAuditLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "administration");

            migrationBuilder.CreateTable(
                name: "audit_logs",
                schema: "administration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    target_type = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    redacted_changes = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_logs", x => x.id);
                    table.CheckConstraint("ck_audit_logs_action_not_blank", "length(btrim(action)) > 0");
                    table.CheckConstraint("ck_audit_logs_correlation_id_not_blank", "length(btrim(correlation_id)) > 0");
                    table.CheckConstraint("ck_audit_logs_target_type_not_blank", "length(btrim(target_type)) > 0");
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_actor_occurred_at",
                schema: "administration",
                table: "audit_logs",
                columns: new[] { "actor_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_target_occurred_at",
                schema: "administration",
                table: "audit_logs",
                columns: new[] { "target_type", "target_id", "occurred_at" });

            migrationBuilder.Sql(
                """
                CREATE FUNCTION administration.reject_audit_log_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    RAISE EXCEPTION 'Audit logs are append-only'
                        USING ERRCODE = '23000';
                END;
                $function$;

                CREATE TRIGGER audit_logs_append_only
                BEFORE UPDATE OR DELETE ON administration.audit_logs
                FOR EACH ROW
                EXECUTE FUNCTION administration.reject_audit_log_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_logs",
                schema: "administration");

            migrationBuilder.Sql(
                "DROP FUNCTION administration.reject_audit_log_mutation();");
        }
    }
}
