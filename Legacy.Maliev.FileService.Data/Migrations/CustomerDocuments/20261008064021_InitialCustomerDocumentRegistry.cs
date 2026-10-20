using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Legacy.Maliev.FileService.Data.Migrations.CustomerDocuments
{
    /// <inheritdoc />
    public partial class InitialCustomerDocumentRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CustomerDocument",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    Visibility = table.Column<int>(type: "integer", nullable: false),
                    ArchivedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerDocument", x => x.Id);
                    table.UniqueConstraint("AK_CustomerDocument_Id_CustomerId", x => new { x.Id, x.CustomerId });
                    table.UniqueConstraint("AK_CustomerDocument_Id_CustomerId_Kind", x => new { x.Id, x.CustomerId, x.Kind });
                    table.CheckConstraint("CK_CustomerDocument_Customer", "\"CustomerId\" > 0");
                    table.CheckConstraint("CK_CustomerDocument_Kind", "\"Kind\" BETWEEN 0 AND 6");
                    table.CheckConstraint("CK_CustomerDocument_Revision", "\"Revision\" > 0");
                    table.CheckConstraint("CK_CustomerDocument_Visibility", "\"Visibility\" BETWEEN 0 AND 1");
                });

            migrationBuilder.CreateTable(
                name: "CustomerDocumentLegalHolds",
                columns: table => new
                {
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerDocumentLegalHolds", x => x.DocumentId);
                    table.ForeignKey(
                        name: "FK_CustomerDocumentLegalHolds_CustomerDocument_DocumentId_Cust~",
                        columns: x => new { x.DocumentId, x.CustomerId },
                        principalTable: "CustomerDocument",
                        principalColumns: new[] { "Id", "CustomerId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomerDocumentUploadCheckpoints",
                columns: table => new
                {
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<int>(type: "integer", nullable: false),
                    Subject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    KeyHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CompletedRevision = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerDocumentUploadCheckpoints", x => x.OperationId);
                    table.CheckConstraint("CK_DocumentUpload_Completed", "(\"State\" = 'Completed' AND \"CompletedRevision\" IS NOT NULL AND \"CompletedRevision\" > 0) OR (\"State\" <> 'Completed' AND \"CompletedRevision\" IS NULL)");
                    table.CheckConstraint("CK_DocumentUpload_Scope", "\"CustomerId\" > 0 AND \"VersionNumber\" > 0");
                    table.CheckConstraint("CK_DocumentUpload_State", "\"State\" IN ('Pending','Unknown','Completed')");
                    table.ForeignKey(
                        name: "FK_CustomerDocumentUploadCheckpoints_CustomerDocument_Document~",
                        columns: x => new { x.DocumentId, x.CustomerId },
                        principalTable: "CustomerDocument",
                        principalColumns: new[] { "Id", "CustomerId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomerDocumentVersion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    ContentSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ActorSubject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    AssociationsSealed = table.Column<bool>(type: "boolean", nullable: false),
                    StorageBucket = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    StorageObjectName = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    StorageGeneration = table.Column<long>(type: "bigint", nullable: false),
                    ContentSize = table.Column<long>(type: "bigint", nullable: false),
                    ContentType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    OriginalFileName = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    ScanOperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScanSourceGeneration = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerDocumentVersion", x => x.Id);
                    table.UniqueConstraint("AK_CustomerDocumentVersion_Id_CustomerId", x => new { x.Id, x.CustomerId });
                    table.UniqueConstraint("AK_CustomerDocumentVersion_Id_DocumentId", x => new { x.Id, x.DocumentId });
                    table.CheckConstraint("CK_CustomerDocumentVersion_Actor", "length(btrim(\"ActorSubject\")) > 0");
                    table.CheckConstraint("CK_CustomerDocumentVersion_Filename", "length(btrim(\"OriginalFileName\")) > 0");
                    table.CheckConstraint("CK_CustomerDocumentVersion_Generation", "\"StorageGeneration\" > 0 AND \"ScanSourceGeneration\" > 0");
                    table.CheckConstraint("CK_CustomerDocumentVersion_Hash", "\"ContentSha256\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_CustomerDocumentVersion_Kind", "\"Kind\" BETWEEN 0 AND 6");
                    table.CheckConstraint("CK_CustomerDocumentVersion_Number", "\"VersionNumber\" > 0");
                    table.CheckConstraint("CK_CustomerDocumentVersion_Revision", "\"Revision\" > 0");
                    table.CheckConstraint("CK_CustomerDocumentVersion_Scan", "\"ScanOperationId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("CK_CustomerDocumentVersion_Size", "\"ContentSize\" BETWEEN 1 AND 20971520");
                    table.CheckConstraint("CK_CustomerDocumentVersion_Storage", "length(btrim(\"StorageBucket\")) > 0 AND \"StorageObjectName\" = 'customer-documents/' || \"CustomerId\"::text || '/' || replace(\"DocumentId\"::text,'-','') || '/' || replace(\"Id\"::text,'-','') || '/original'");
                    table.CheckConstraint("CK_CustomerDocumentVersion_Type", "\"ContentType\" IN ('application/pdf','image/png','image/jpeg')");
                    table.ForeignKey(
                        name: "FK_CustomerDocumentVersion_CustomerDocument_DocumentId_Custome~",
                        columns: x => new { x.DocumentId, x.CustomerId, x.Kind },
                        principalTable: "CustomerDocument",
                        principalColumns: new[] { "Id", "CustomerId", "Kind" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomerDocumentAssociation",
                columns: table => new
                {
                    VersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    ResourceId = table.Column<int>(type: "integer", nullable: false),
                    CustomerId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerDocumentAssociation", x => new { x.VersionId, x.Kind, x.ResourceId });
                    table.CheckConstraint("CK_CustomerDocumentAssociation_Customer", "\"Kind\" <> 0 OR \"ResourceId\" = \"CustomerId\"");
                    table.CheckConstraint("CK_CustomerDocumentAssociation_Id", "\"ResourceId\" > 0");
                    table.CheckConstraint("CK_CustomerDocumentAssociation_Kind", "\"Kind\" BETWEEN 0 AND 5");
                    table.ForeignKey(
                        name: "FK_CustomerDocumentAssociation_CustomerDocumentVersion_Version~",
                        columns: x => new { x.VersionId, x.CustomerId },
                        principalTable: "CustomerDocumentVersion",
                        principalColumns: new[] { "Id", "CustomerId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomerDocumentAudit",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActorSubject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerDocumentAudit", x => x.Id);
                    table.CheckConstraint("CK_CustomerDocumentAudit_Actor", "length(btrim(\"ActorSubject\")) > 0");
                    table.CheckConstraint("CK_CustomerDocumentAudit_Revision", "\"Revision\" > 0");
                    table.ForeignKey(
                        name: "FK_CustomerDocumentAudit_CustomerDocumentVersion_VersionId_Doc~",
                        columns: x => new { x.VersionId, x.DocumentId },
                        principalTable: "CustomerDocumentVersion",
                        principalColumns: new[] { "Id", "DocumentId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerDocumentAudit_CustomerDocument_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "CustomerDocument",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomerDocumentVerificationEvidence",
                columns: table => new
                {
                    VersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    VerifiedBySubject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    VerifiedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerDocumentVerificationEvidence", x => new { x.VersionId, x.Revision });
                    table.CheckConstraint("CK_CustomerDocumentVerificationEvidence_Revision", "\"Revision\" > 0");
                    table.CheckConstraint("CK_CustomerDocumentVerificationEvidence_Status", "\"Status\" BETWEEN 0 AND 2");
                    table.CheckConstraint("CK_CustomerDocumentVerificationEvidence_Verifier", "(\"Status\" = 0 AND \"VerifiedBySubject\" IS NULL AND \"VerifiedAtUtc\" IS NULL) OR (\"Status\" IN (1,2) AND \"VerifiedBySubject\" IS NOT NULL AND length(btrim(\"VerifiedBySubject\")) > 0 AND \"VerifiedAtUtc\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_CustomerDocumentVerificationEvidence_CustomerDocumentVersio~",
                        column: x => x.VersionId,
                        principalTable: "CustomerDocumentVersion",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "NdaRecord",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<int>(type: "integer", nullable: false),
                    PartyOne = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    PartyTwo = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    EffectiveAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RenewalAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SurvivalKind = table.Column<int>(type: "integer", nullable: false),
                    SurvivalEndsAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ResponsibleEmployeeSubject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    VerifiedBySubject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    VerifiedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    VerificationRevision = table.Column<long>(type: "bigint", nullable: false),
                    SupersedesNdaId = table.Column<Guid>(type: "uuid", nullable: true),
                    CoverageSealed = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NdaRecord", x => x.Id);
                    table.UniqueConstraint("AK_NdaRecord_Id_CustomerId", x => new { x.Id, x.CustomerId });
                    table.UniqueConstraint("AK_NdaRecord_Id_DocumentId_CustomerId", x => new { x.Id, x.DocumentId, x.CustomerId });
                    table.UniqueConstraint("AK_NdaRecord_Id_VersionId_VerificationRevision", x => new { x.Id, x.VersionId, x.VerificationRevision });
                    table.UniqueConstraint("AK_NdaRecord_Id_VersionId_VerificationRevision_ResponsibleEmpl~", x => new { x.Id, x.VersionId, x.VerificationRevision, x.ResponsibleEmployeeSubject });
                    table.CheckConstraint("CK_NdaRecord_Customer", "\"CustomerId\" > 0");
                    table.CheckConstraint("CK_NdaRecord_Dates", "(\"ExpiresAtUtc\" IS NULL OR \"ExpiresAtUtc\" > \"EffectiveAtUtc\") AND (\"RenewalAtUtc\" IS NULL OR \"RenewalAtUtc\" >= \"EffectiveAtUtc\")");
                    table.CheckConstraint("CK_NdaRecord_Parties", "length(btrim(\"PartyOne\")) > 0 AND length(btrim(\"PartyTwo\")) > 0 AND lower(btrim(\"PartyOne\")) <> lower(btrim(\"PartyTwo\"))");
                    table.CheckConstraint("CK_NdaRecord_Revision", "\"VerificationRevision\" > 0");
                    table.CheckConstraint("CK_NdaRecord_Staff", "length(btrim(\"ResponsibleEmployeeSubject\")) > 0 AND length(btrim(\"VerifiedBySubject\")) > 0");
                    table.CheckConstraint("CK_NdaRecord_Survival", "(\"SurvivalKind\" IN (0,1) AND \"SurvivalEndsAtUtc\" IS NULL) OR (\"SurvivalKind\" = 2 AND \"SurvivalEndsAtUtc\" IS NOT NULL AND \"SurvivalEndsAtUtc\" >= COALESCE(\"ExpiresAtUtc\",\"EffectiveAtUtc\"))");
                    table.ForeignKey(
                        name: "FK_NdaRecord_CustomerDocumentVerificationEvidence_VersionId_Ve~",
                        columns: x => new { x.VersionId, x.VerificationRevision },
                        principalTable: "CustomerDocumentVerificationEvidence",
                        principalColumns: new[] { "VersionId", "Revision" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_NdaRecord_CustomerDocumentVersion_VersionId_CustomerId",
                        columns: x => new { x.VersionId, x.CustomerId },
                        principalTable: "CustomerDocumentVersion",
                        principalColumns: new[] { "Id", "CustomerId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_NdaRecord_CustomerDocumentVersion_VersionId_DocumentId",
                        columns: x => new { x.VersionId, x.DocumentId },
                        principalTable: "CustomerDocumentVersion",
                        principalColumns: new[] { "Id", "DocumentId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_NdaRecord_NdaRecord_SupersedesNdaId_DocumentId_CustomerId",
                        columns: x => new { x.SupersedesNdaId, x.DocumentId, x.CustomerId },
                        principalTable: "NdaRecord",
                        principalColumns: new[] { "Id", "DocumentId", "CustomerId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "InternalNdaReminder",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    NdaId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    RenewalRevision = table.Column<long>(type: "bigint", nullable: false),
                    LeadDays = table.Column<int>(type: "integer", nullable: false),
                    ResponsibleEmployeeSubject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DueAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    CancelledAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InternalNdaReminder", x => x.Id);
                    table.CheckConstraint("CK_InternalNdaReminder_Lead", "\"LeadDays\" BETWEEN 0 AND 3660");
                    table.CheckConstraint("CK_InternalNdaReminder_Recipient", "length(btrim(\"ResponsibleEmployeeSubject\")) > 0");
                    table.CheckConstraint("CK_InternalNdaReminder_State", "\"State\" BETWEEN 0 AND 2 AND ((\"State\" = 2 AND \"CancelledAtUtc\" IS NOT NULL) OR (\"State\" <> 2 AND \"CancelledAtUtc\" IS NULL))");
                    table.ForeignKey(
                        name: "FK_InternalNdaReminder_NdaRecord_NdaId_VersionId_RenewalRevisi~",
                        columns: x => new { x.NdaId, x.VersionId, x.RenewalRevision, x.ResponsibleEmployeeSubject },
                        principalTable: "NdaRecord",
                        principalColumns: new[] { "Id", "VersionId", "VerificationRevision", "ResponsibleEmployeeSubject" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "NdaCoverage",
                columns: table => new
                {
                    NdaId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    ResourceId = table.Column<int>(type: "integer", nullable: false),
                    CustomerId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NdaCoverage", x => new { x.NdaId, x.Kind, x.ResourceId });
                    table.CheckConstraint("CK_NdaCoverage_Customer", "\"Kind\" <> 0 OR \"ResourceId\" = \"CustomerId\"");
                    table.CheckConstraint("CK_NdaCoverage_Identity", "\"ResourceId\" > 0 AND \"Kind\" BETWEEN 0 AND 5");
                    table.ForeignKey(
                        name: "FK_NdaCoverage_NdaRecord_NdaId_CustomerId",
                        columns: x => new { x.NdaId, x.CustomerId },
                        principalTable: "NdaRecord",
                        principalColumns: new[] { "Id", "CustomerId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerDocumentAssociation_VersionId",
                table: "CustomerDocumentAssociation",
                column: "VersionId",
                unique: true,
                filter: "\"Kind\" = 2");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerDocumentAssociation_VersionId_CustomerId",
                table: "CustomerDocumentAssociation",
                columns: new[] { "VersionId", "CustomerId" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerDocumentAudit_DocumentId",
                table: "CustomerDocumentAudit",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerDocumentAudit_VersionId_DocumentId",
                table: "CustomerDocumentAudit",
                columns: new[] { "VersionId", "DocumentId" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerDocumentLegalHolds_DocumentId_CustomerId",
                table: "CustomerDocumentLegalHolds",
                columns: new[] { "DocumentId", "CustomerId" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerDocumentUploadCheckpoints_CustomerId_Subject_KeyHash",
                table: "CustomerDocumentUploadCheckpoints",
                columns: new[] { "CustomerId", "Subject", "KeyHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerDocumentUploadCheckpoints_DocumentId_CustomerId",
                table: "CustomerDocumentUploadCheckpoints",
                columns: new[] { "DocumentId", "CustomerId" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerDocumentUploadCheckpoints_DocumentId_VersionNumber",
                table: "CustomerDocumentUploadCheckpoints",
                columns: new[] { "DocumentId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerDocumentUploadCheckpoints_VersionId",
                table: "CustomerDocumentUploadCheckpoints",
                column: "VersionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerDocumentVersion_DocumentId_CustomerId_Kind",
                table: "CustomerDocumentVersion",
                columns: new[] { "DocumentId", "CustomerId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerDocumentVersion_DocumentId_VersionNumber",
                table: "CustomerDocumentVersion",
                columns: new[] { "DocumentId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InternalNdaReminder_NdaId_VersionId_RenewalRevision_LeadDays",
                table: "InternalNdaReminder",
                columns: new[] { "NdaId", "VersionId", "RenewalRevision", "LeadDays" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InternalNdaReminder_NdaId_VersionId_RenewalRevision_Respons~",
                table: "InternalNdaReminder",
                columns: new[] { "NdaId", "VersionId", "RenewalRevision", "ResponsibleEmployeeSubject" });

            migrationBuilder.CreateIndex(
                name: "IX_InternalNdaReminder_ResponsibleEmployeeSubject_DueAtUtc",
                table: "InternalNdaReminder",
                columns: new[] { "ResponsibleEmployeeSubject", "DueAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_NdaCoverage_CustomerId_Kind_ResourceId",
                table: "NdaCoverage",
                columns: new[] { "CustomerId", "Kind", "ResourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_NdaCoverage_NdaId_CustomerId",
                table: "NdaCoverage",
                columns: new[] { "NdaId", "CustomerId" });

            migrationBuilder.CreateIndex(
                name: "IX_NdaRecord_DocumentId_VerificationRevision",
                table: "NdaRecord",
                columns: new[] { "DocumentId", "VerificationRevision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NdaRecord_SupersedesNdaId",
                table: "NdaRecord",
                column: "SupersedesNdaId",
                unique: true,
                filter: "\"SupersedesNdaId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_NdaRecord_SupersedesNdaId_DocumentId_CustomerId",
                table: "NdaRecord",
                columns: new[] { "SupersedesNdaId", "DocumentId", "CustomerId" });

            migrationBuilder.CreateIndex(
                name: "IX_NdaRecord_VersionId_CustomerId",
                table: "NdaRecord",
                columns: new[] { "VersionId", "CustomerId" });

            migrationBuilder.CreateIndex(
                name: "IX_NdaRecord_VersionId_DocumentId",
                table: "NdaRecord",
                columns: new[] { "VersionId", "DocumentId" });

            migrationBuilder.CreateIndex(
                name: "IX_NdaRecord_VersionId_VerificationRevision",
                table: "NdaRecord",
                columns: new[] { "VersionId", "VerificationRevision" });
            migrationBuilder.Sql("""
                CREATE FUNCTION customer_document_evidence_append_only() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'Customer document evidence is append-only' USING ERRCODE='23514'; END; $$;
                CREATE FUNCTION customer_document_version_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP='INSERT' THEN
                        IF NEW."AssociationsSealed" THEN RAISE EXCEPTION 'Version must be finalized atomically' USING ERRCODE='23514'; END IF;
                        RETURN NEW;
                    END IF;
                    IF TG_OP='UPDATE' AND NOT OLD."AssociationsSealed" AND NEW."AssociationsSealed"
                       AND (to_jsonb(NEW)-'AssociationsSealed')=(to_jsonb(OLD)-'AssociationsSealed') THEN RETURN NEW; END IF;
                    RAISE EXCEPTION 'Version evidence and finalization seal are immutable' USING ERRCODE='23514';
                END; $$;
                CREATE TRIGGER customer_document_version_immutable BEFORE INSERT OR UPDATE OR DELETE ON "CustomerDocumentVersion"
                    FOR EACH ROW EXECUTE FUNCTION customer_document_version_immutable();
                CREATE FUNCTION customer_document_association_insert() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE sealed boolean;
                BEGIN
                    SELECT "AssociationsSealed" INTO sealed FROM "CustomerDocumentVersion" WHERE "Id"=NEW."VersionId" FOR UPDATE;
                    IF sealed THEN RAISE EXCEPTION 'Finalized associations are immutable' USING ERRCODE='23514'; END IF;
                    RETURN NEW;
                END; $$;
                CREATE TRIGGER customer_document_association_insert BEFORE INSERT ON "CustomerDocumentAssociation"
                    FOR EACH ROW EXECUTE FUNCTION customer_document_association_insert();
                CREATE TRIGGER customer_document_association_immutable BEFORE UPDATE OR DELETE ON "CustomerDocumentAssociation"
                    FOR EACH ROW EXECUTE FUNCTION customer_document_evidence_append_only();
                CREATE TRIGGER customer_document_audit_immutable BEFORE UPDATE OR DELETE ON "CustomerDocumentAudit"
                    FOR EACH ROW EXECUTE FUNCTION customer_document_evidence_append_only();
                CREATE TRIGGER customer_document_verification_immutable BEFORE UPDATE OR DELETE ON "CustomerDocumentVerificationEvidence"
                    FOR EACH ROW EXECUTE FUNCTION customer_document_evidence_append_only();
                CREATE FUNCTION customer_document_identity_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP='DELETE' OR NEW."Id"<>OLD."Id" OR NEW."CustomerId"<>OLD."CustomerId"
                       OR NEW."Kind"<>OLD."Kind" OR NEW."Title"<>OLD."Title" OR NEW."Visibility"<>OLD."Visibility"
                       OR NEW."Revision"<=OLD."Revision" THEN
                        RAISE EXCEPTION 'Customer document identity is immutable; revisions must advance' USING ERRCODE='23514';
                    END IF;
                    RETURN NEW;
                END; $$;
                CREATE TRIGGER customer_document_identity_immutable BEFORE UPDATE OR DELETE ON "CustomerDocument"
                    FOR EACH ROW EXECUTE FUNCTION customer_document_identity_immutable();
                CREATE FUNCTION customer_document_checkpoint_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP='DELETE' OR (to_jsonb(NEW)-'State'-'CompletedRevision')<>(to_jsonb(OLD)-'State'-'CompletedRevision')
                       OR OLD."State"='Completed' OR (OLD."State"='Unknown' AND NEW."State"<>'Completed') THEN
                        RAISE EXCEPTION 'Upload reservation identity is immutable' USING ERRCODE='23514';
                    END IF;
                    IF (NEW."State"='Completed' AND (NEW."CompletedRevision" IS NULL OR NEW."CompletedRevision"<=0))
                       OR (NEW."State"<>'Completed' AND NEW."CompletedRevision" IS NOT NULL) THEN
                        RAISE EXCEPTION 'Upload outcome evidence is invalid' USING ERRCODE='23514';
                    END IF;
                    RETURN NEW;
                END; $$;
                CREATE TRIGGER customer_document_checkpoint_immutable BEFORE UPDATE OR DELETE ON "CustomerDocumentUploadCheckpoints"
                    FOR EACH ROW EXECUTE FUNCTION customer_document_checkpoint_immutable();
                CREATE FUNCTION customer_document_hold_lock() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE document_id uuid;
                BEGIN
                    document_id:=CASE WHEN TG_OP='DELETE' THEN OLD."DocumentId" ELSE NEW."DocumentId" END;
                    PERFORM 1 FROM "CustomerDocument" WHERE "Id"=document_id FOR UPDATE;
                    IF TG_OP='UPDATE' AND (NEW."DocumentId"<>OLD."DocumentId" OR NEW."CustomerId"<>OLD."CustomerId") THEN
                        RAISE EXCEPTION 'Legal hold identity is immutable' USING ERRCODE='23514';
                    END IF;
                    IF TG_OP='DELETE' THEN RETURN OLD; END IF;
                    RETURN NEW;
                END; $$;
                CREATE TRIGGER customer_document_hold_lock BEFORE INSERT OR UPDATE OR DELETE ON "CustomerDocumentLegalHolds"
                    FOR EACH ROW EXECUTE FUNCTION customer_document_hold_lock();
                CREATE FUNCTION customer_document_nda_record_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP='INSERT' THEN
                        IF NEW."CoverageSealed" THEN RAISE EXCEPTION 'NDA coverage must be finalized atomically' USING ERRCODE='23514'; END IF;
                        RETURN NEW;
                    END IF;
                    IF TG_OP='UPDATE' AND NOT OLD."CoverageSealed" AND NEW."CoverageSealed"
                       AND (to_jsonb(NEW)-'CoverageSealed')=(to_jsonb(OLD)-'CoverageSealed') THEN RETURN NEW; END IF;
                    RAISE EXCEPTION 'NDA evidence and coverage seal are immutable' USING ERRCODE='23514';
                END; $$;
                CREATE TRIGGER customer_document_nda_record_immutable BEFORE INSERT OR UPDATE OR DELETE ON "NdaRecord"
                    FOR EACH ROW EXECUTE FUNCTION customer_document_nda_record_immutable();
                CREATE FUNCTION customer_document_nda_coverage_insert() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE sealed boolean;
                BEGIN
                    SELECT "CoverageSealed" INTO sealed FROM "NdaRecord" WHERE "Id"=NEW."NdaId" FOR UPDATE;
                    IF sealed THEN RAISE EXCEPTION 'Finalized NDA coverage is immutable' USING ERRCODE='23514'; END IF;
                    RETURN NEW;
                END; $$;
                CREATE TRIGGER customer_document_nda_coverage_insert BEFORE INSERT ON "NdaCoverage"
                    FOR EACH ROW EXECUTE FUNCTION customer_document_nda_coverage_insert();
                CREATE TRIGGER customer_document_nda_coverage_immutable BEFORE UPDATE OR DELETE ON "NdaCoverage"
                    FOR EACH ROW EXECUTE FUNCTION customer_document_evidence_append_only();
                CREATE FUNCTION customer_document_reminder_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP='UPDATE' AND OLD."State" IN (0,1) AND NEW."State"=2 AND NEW."CancelledAtUtc" IS NOT NULL
                       AND (to_jsonb(NEW)-'State'-'CancelledAtUtc')=(to_jsonb(OLD)-'State'-'CancelledAtUtc') THEN RETURN NEW; END IF;
                    IF TG_OP='UPDATE' AND OLD."State"=0 AND NEW."State"=1 AND NEW."CancelledAtUtc" IS NULL
                       AND (to_jsonb(NEW)-'State')=(to_jsonb(OLD)-'State') THEN RETURN NEW; END IF;
                    RAISE EXCEPTION 'Reminder identity is immutable and cancellation is irreversible' USING ERRCODE='23514';
                END; $$;
                CREATE TRIGGER customer_document_reminder_immutable BEFORE UPDATE OR DELETE ON "InternalNdaReminder"
                    FOR EACH ROW EXECUTE FUNCTION customer_document_reminder_immutable();
                """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CustomerDocumentAssociation");

            migrationBuilder.DropTable(
                name: "CustomerDocumentAudit");

            migrationBuilder.DropTable(
                name: "CustomerDocumentLegalHolds");

            migrationBuilder.DropTable(
                name: "CustomerDocumentUploadCheckpoints");

            migrationBuilder.DropTable(
                name: "InternalNdaReminder");

            migrationBuilder.DropTable(
                name: "NdaCoverage");

            migrationBuilder.DropTable(
                name: "NdaRecord");

            migrationBuilder.DropTable(
                name: "CustomerDocumentVerificationEvidence");

            migrationBuilder.DropTable(
                name: "CustomerDocumentVersion");

            migrationBuilder.DropTable(
                name: "CustomerDocument");
            migrationBuilder.Sql("""
                DROP FUNCTION customer_document_evidence_append_only();
                DROP FUNCTION customer_document_version_immutable();
                DROP FUNCTION customer_document_association_insert();
                DROP FUNCTION customer_document_identity_immutable();
                DROP FUNCTION customer_document_checkpoint_immutable();
                DROP FUNCTION customer_document_hold_lock();
                DROP FUNCTION customer_document_reminder_immutable();
                DROP FUNCTION customer_document_nda_record_immutable();
                DROP FUNCTION customer_document_nda_coverage_insert();
                """);

        }
    }
}
