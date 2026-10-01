using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Legacy.Maliev.FileService.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStorageMoveJournal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StorageMoveJournal",
                columns: table => new
                {
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScanClean = table.Column<bool>(type: "boolean", nullable: false),
                    SourceBucket = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    SourceObjectName = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    SourceGeneration = table.Column<long>(type: "bigint", nullable: false),
                    DestinationBucket = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    DestinationObjectName = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    DestinationGeneration = table.Column<long>(type: "bigint", nullable: true),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StorageMoveJournal", x => x.OperationId);
                    table.CheckConstraint("CK_StorageMoveJournal_DestinationGeneration", "\"DestinationGeneration\" IS NULL OR \"DestinationGeneration\" > 0");
                    table.CheckConstraint("CK_StorageMoveJournal_SourceGeneration", "\"SourceGeneration\" > 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_StorageMoveJournal_DestinationBucket_DestinationObjectName",
                table: "StorageMoveJournal",
                columns: new[] { "DestinationBucket", "DestinationObjectName" });

            migrationBuilder.CreateIndex(
                name: "IX_StorageMoveJournal_SourceBucket_SourceObjectName",
                table: "StorageMoveJournal",
                columns: new[] { "SourceBucket", "SourceObjectName" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Storage move recovery authority is forward-only and cannot be dropped by downgrade.");
        }
    }
}
