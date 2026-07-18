using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Legacy.Maliev.FileService.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInstantQuoteObjectBuckets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "LOCK TABLE \"InstantQuoteUploadFile\" IN ACCESS EXCLUSIVE MODE;");

            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "InstantQuoteUploadFile" LIMIT 1) THEN
                        RAISE EXCEPTION 'InstantQuoteUploadFile must be empty before adding durable object authority fields';
                    END IF;
                END $$;
                """);

            migrationBuilder.AddColumn<string>(
                name: "FinalBucket",
                table: "InstantQuoteUploadFile",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TemporaryBucket",
                table: "InstantQuoteUploadFile",
                type: "character varying(255)",
                maxLength: 255,
                nullable: false);

            migrationBuilder.AddColumn<Guid>(
                name: "FinalizedQuotationRequestId",
                table: "InstantQuoteUploadFile",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FinalBucket",
                table: "InstantQuoteUploadFile");

            migrationBuilder.DropColumn(
                name: "TemporaryBucket",
                table: "InstantQuoteUploadFile");

            migrationBuilder.DropColumn(
                name: "FinalizedQuotationRequestId",
                table: "InstantQuoteUploadFile");
        }
    }
}
