using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Legacy.Maliev.FileService.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceQuotationRequestIdsWithInteger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            LockAndRequireEmptyWorkflowTables(migrationBuilder);

            migrationBuilder.DropColumn(
                name: "FinalizedQuotationRequestId",
                table: "InstantQuoteUploadFile");

            migrationBuilder.DropColumn(
                name: "QuotationRequestId",
                table: "InstantQuoteFinalization");

            migrationBuilder.AddColumn<int>(
                name: "FinalizedQuotationRequestId",
                table: "InstantQuoteUploadFile",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "QuotationRequestId",
                table: "InstantQuoteFinalization",
                type: "integer",
                nullable: false);

            migrationBuilder.AddCheckConstraint(
                name: "CK_InstantQuoteUploadFile_FinalizedQuotationRequestId_Positive",
                table: "InstantQuoteUploadFile",
                sql: "\"FinalizedQuotationRequestId\" IS NULL OR \"FinalizedQuotationRequestId\" > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_InstantQuoteFinalization_QuotationRequestId_Positive",
                table: "InstantQuoteFinalization",
                sql: "\"QuotationRequestId\" > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            LockAndRequireEmptyWorkflowTables(migrationBuilder);

            migrationBuilder.DropCheckConstraint(
                name: "CK_InstantQuoteUploadFile_FinalizedQuotationRequestId_Positive",
                table: "InstantQuoteUploadFile");

            migrationBuilder.DropCheckConstraint(
                name: "CK_InstantQuoteFinalization_QuotationRequestId_Positive",
                table: "InstantQuoteFinalization");

            migrationBuilder.DropColumn(
                name: "FinalizedQuotationRequestId",
                table: "InstantQuoteUploadFile");

            migrationBuilder.DropColumn(
                name: "QuotationRequestId",
                table: "InstantQuoteFinalization");

            migrationBuilder.AddColumn<Guid>(
                name: "FinalizedQuotationRequestId",
                table: "InstantQuoteUploadFile",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "QuotationRequestId",
                table: "InstantQuoteFinalization",
                type: "uuid",
                nullable: false);
        }

        private static void LockAndRequireEmptyWorkflowTables(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "LOCK TABLE \"InstantQuoteUploadFile\", \"InstantQuoteFinalization\" IN ACCESS EXCLUSIVE MODE;");
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "InstantQuoteUploadFile" LIMIT 1)
                       OR EXISTS (SELECT 1 FROM "InstantQuoteFinalization" LIMIT 1) THEN
                        RAISE EXCEPTION 'Instant quotation workflow tables must be empty before replacing quotation request identifiers';
                    END IF;
                END $$;
                """);
        }
    }
}
