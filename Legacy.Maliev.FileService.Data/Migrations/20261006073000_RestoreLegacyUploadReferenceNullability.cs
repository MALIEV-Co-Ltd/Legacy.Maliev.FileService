using Microsoft.EntityFrameworkCore.Migrations;

namespace Legacy.Maliev.FileService.Data.Migrations;

/// <inheritdoc />
public partial class RestoreLegacyUploadReferenceNullability : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<string>(
            name: "Bucket",
            table: "Upload",
            type: "character varying(50)",
            maxLength: 50,
            nullable: true,
            oldClrType: typeof(string),
            oldType: "character varying(50)",
            oldMaxLength: 50);
        migrationBuilder.AlterColumn<string>(
            name: "ContentType",
            table: "Upload",
            type: "character varying(50)",
            maxLength: 50,
            nullable: true,
            oldClrType: typeof(string),
            oldType: "character varying(50)",
            oldMaxLength: 50);
        migrationBuilder.AlterColumn<string>(
            name: "Name",
            table: "Upload",
            type: "text",
            nullable: true,
            oldClrType: typeof(string),
            oldType: "text");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Legacy upload fields may be unknown; restoring NOT NULL requires an explicit data review.");
}
