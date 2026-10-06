using Microsoft.EntityFrameworkCore.Migrations;

namespace Legacy.Maliev.FileService.Data.Migrations;

/// <inheritdoc />
public partial class RestoreLegacyUploadSizeNullability : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<long>(
            name: "Size",
            table: "Upload",
            type: "bigint",
            nullable: true,
            oldClrType: typeof(long),
            oldType: "bigint");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Legacy upload sizes may be unknown; restoring NOT NULL requires an explicit data review.");
}
