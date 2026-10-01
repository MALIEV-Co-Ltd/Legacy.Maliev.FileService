using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Legacy.Maliev.FileService.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddQuarantineUploadIntent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "QuarantineUploadIntent",
                columns: table => new
                {
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentOperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Bucket = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ObjectName = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    DeclaredSize = table.Column<long>(type: "bigint", nullable: false),
                    AcknowledgedGeneration = table.Column<long>(type: "bigint", nullable: true),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuarantineUploadIntent", x => x.OperationId);
                    table.CheckConstraint("CK_QuarantineUploadIntent_DeclaredSize", "\"DeclaredSize\" > 0");
                    table.CheckConstraint("CK_QuarantineUploadIntent_Generation", "\"AcknowledgedGeneration\" IS NULL OR \"AcknowledgedGeneration\" > 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_QuarantineUploadIntent_Bucket_ObjectName",
                table: "QuarantineUploadIntent",
                columns: new[] { "Bucket", "ObjectName" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Private quarantine recovery authority is forward-only and cannot be dropped by downgrade.");
        }
    }
}
