using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddChapterPageUploadFingerprint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "upload_created_at",
                schema: "catalog",
                table: "chapter_pages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "upload_fingerprint",
                schema: "catalog",
                table: "chapter_pages",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE catalog.chapter_pages
                SET upload_created_at = CURRENT_TIMESTAMP,
                    upload_fingerprint = 'legacy:' || id::text;
                """);

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "upload_created_at",
                schema: "catalog",
                table: "chapter_pages",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "upload_fingerprint",
                schema: "catalog",
                table: "chapter_pages",
                type: "character varying(80)",
                maxLength: 80,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(80)",
                oldMaxLength: 80,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_chapter_pages_asset_status_upload_created_at",
                schema: "catalog",
                table: "chapter_pages",
                columns: new[] { "asset_status", "upload_created_at" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_chapter_pages_upload_fingerprint",
                schema: "catalog",
                table: "chapter_pages",
                sql: "upload_fingerprint ~ '^(sha256:[0-9a-f]{64}|legacy:[0-9a-f-]{36})$'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_chapter_pages_asset_status_upload_created_at",
                schema: "catalog",
                table: "chapter_pages");

            migrationBuilder.DropCheckConstraint(
                name: "ck_chapter_pages_upload_fingerprint",
                schema: "catalog",
                table: "chapter_pages");

            migrationBuilder.DropColumn(
                name: "upload_created_at",
                schema: "catalog",
                table: "chapter_pages");

            migrationBuilder.DropColumn(
                name: "upload_fingerprint",
                schema: "catalog",
                table: "chapter_pages");
        }
    }
}
