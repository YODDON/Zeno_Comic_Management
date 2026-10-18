using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicManagement.Persistence.Migrations;

public partial class AddMissionReceiptPayloadFingerprint : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "payload_fingerprint",
            schema: "missions",
            table: "action_receipts",
            type: "character(64)",
            fixedLength: true,
            maxLength: 64,
            nullable: true);

        migrationBuilder.Sql(
            """
            UPDATE missions.action_receipts
            SET payload_fingerprint = encode(
                sha256(convert_to(
                    source_event_id::text || '|' ||
                    source_event_type || '|' ||
                    user_id::text || '|' ||
                    coalesce(subject_id::text, '') || '|' ||
                    occurred_at::text,
                    'UTF8')),
                'hex')
            WHERE payload_fingerprint IS NULL;
            """);

        migrationBuilder.AlterColumn<string>(
            name: "payload_fingerprint",
            schema: "missions",
            table: "action_receipts",
            type: "character(64)",
            fixedLength: true,
            maxLength: 64,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "character(64)",
            oldFixedLength: true,
            oldMaxLength: 64,
            oldNullable: true);

        migrationBuilder.AddCheckConstraint(
            name: "ck_action_receipts_payload_fingerprint",
            schema: "missions",
            table: "action_receipts",
            sql: "payload_fingerprint ~ '^[0-9a-f]{64}$'");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_action_receipts_payload_fingerprint",
            schema: "missions",
            table: "action_receipts");

        migrationBuilder.DropColumn(
            name: "payload_fingerprint",
            schema: "missions",
            table: "action_receipts");
    }
}
