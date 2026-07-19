using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Legacy.Maliev.FileService.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInstantQuoteUploadWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InstantQuoteUploadSession",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerSubject = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    IsAuthenticated = table.Column<bool>(type: "boolean", nullable: false),
                    TokenHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstantQuoteUploadSession", x => x.Id);
                    table.CheckConstraint("CK_InstantQuoteUploadSession_TokenHash_Length", "octet_length(\"TokenHash\") = 32");
                });

            migrationBuilder.CreateTable(
                name: "InstantQuoteFinalization",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKeyHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    RequestFingerprint = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    QuotationRequestId = table.Column<int>(type: "integer", nullable: false),
                    SelectedFileIds = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstantQuoteFinalization", x => x.Id);
                    table.CheckConstraint("CK_InstantQuoteFinalization_Fingerprint", "\"RequestFingerprint\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_InstantQuoteFinalization_KeyHash_Length", "octet_length(\"IdempotencyKeyHash\") = 32");
                    table.CheckConstraint("CK_InstantQuoteFinalization_QuotationRequestId_Positive", "\"QuotationRequestId\" > 0");
                    table.ForeignKey(
                        name: "FK_InstantQuoteFinalization_InstantQuoteUploadSession_SessionId",
                        column: x => x.SessionId,
                        principalTable: "InstantQuoteUploadSession",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InstantQuoteUploadFile",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKeyHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    RequestFingerprint = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    OriginalFileName = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    ValidatedExtension = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ValidatedContentType = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ExpectedSha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    ActualSha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    ActualSizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    GcsGeneration = table.Column<long>(type: "bigint", nullable: true),
                    TemporaryCleanupCompleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    TemporaryBucket = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    TemporaryObjectName = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    FinalBucket = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    FinalObjectName = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    FinalizedQuotationRequestId = table.Column<int>(type: "integer", nullable: true),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstantQuoteUploadFile", x => x.Id);
                    table.CheckConstraint("CK_InstantQuoteUploadFile_ActualSha256", "\"ActualSha256\" IS NULL OR \"ActualSha256\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_InstantQuoteUploadFile_ExpectedSha256", "\"ExpectedSha256\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_InstantQuoteUploadFile_FinalizedQuotationRequestId_Positive", "\"FinalizedQuotationRequestId\" IS NULL OR \"FinalizedQuotationRequestId\" > 0");
                    table.CheckConstraint("CK_InstantQuoteUploadFile_Fingerprint", "\"RequestFingerprint\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_InstantQuoteUploadFile_KeyHash_Length", "octet_length(\"IdempotencyKeyHash\") = 32");
                    table.ForeignKey(
                        name: "FK_InstantQuoteUploadFile_InstantQuoteUploadSession_SessionId",
                        column: x => x.SessionId,
                        principalTable: "InstantQuoteUploadSession",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InstantQuoteFinalization_SessionId_IdempotencyKeyHash",
                table: "InstantQuoteFinalization",
                columns: new[] { "SessionId", "IdempotencyKeyHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InstantQuoteUploadFile_SessionId_IdempotencyKeyHash",
                table: "InstantQuoteUploadFile",
                columns: new[] { "SessionId", "IdempotencyKeyHash" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InstantQuoteFinalization");

            migrationBuilder.DropTable(
                name: "InstantQuoteUploadFile");

            migrationBuilder.DropTable(
                name: "InstantQuoteUploadSession");
        }
    }
}
