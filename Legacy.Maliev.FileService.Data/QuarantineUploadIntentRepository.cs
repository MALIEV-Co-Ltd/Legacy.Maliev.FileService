using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Services;
using Microsoft.EntityFrameworkCore;

namespace Legacy.Maliev.FileService.Data;

/// <summary>PostgreSQL authority for an initial private upload, without retry or automatic repair.</summary>
public sealed class QuarantineUploadIntentRepository(FileDbContext db, TimeProvider clock) : IQuarantineUploadIntent
{
    /// <inheritdoc />
    public async Task PrepareAsync(Guid operationId, Guid parentOperationId, string bucket, string objectName,
        string contentType, long declaredSize, CancellationToken cancellationToken)
    {
        if (operationId == Guid.Empty || parentOperationId == Guid.Empty) throw new ArgumentException("An upload operation identity is required.");
        if (declaredSize <= 0) throw new ArgumentOutOfRangeException(nameof(declaredSize));
        await EnsurePhysicalSchemaAsync(db, cancellationToken);
        var now = clock.GetUtcNow();
        const string state = "Pending";
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "QuarantineUploadIntent" ("OperationId", "ParentOperationId", "Bucket", "ObjectName",
                "ContentType", "DeclaredSize", "State", "CreatedAt", "ModifiedAt")
            VALUES ({operationId}, {parentOperationId}, {bucket}, {objectName}, {contentType}, {declaredSize}, {state}, {now}, {now})
            ON CONFLICT ("OperationId") DO NOTHING
            """, cancellationToken);
        if (inserted != 1) throw new UploadOutcomeUnknownException("Private upload already has durable authority; reconciliation is required.");
    }

    /// <inheritdoc />
    public async Task AcknowledgeAsync(Guid operationId, long generation, CancellationToken cancellationToken)
    {
        if (generation <= 0) throw new ArgumentOutOfRangeException(nameof(generation));
        var updated = await db.QuarantineUploadIntents.Where(row => row.OperationId == operationId && row.State == "Pending" && row.AcknowledgedGeneration == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.AcknowledgedGeneration, generation)
                .SetProperty(row => row.State, "Uploaded").SetProperty(row => row.ModifiedAt, clock.GetUtcNow()), cancellationToken);
        if (updated != 1) throw new UploadOutcomeUnknownException("Private upload acknowledgment requires reconciliation.");
    }

    /// <inheritdoc />
    public Task UnknownAsync(Guid operationId, CancellationToken cancellationToken) =>
        db.QuarantineUploadIntents.Where(row => row.OperationId == operationId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.State, "Unknown")
                .SetProperty(row => row.ModifiedAt, clock.GetUtcNow()), cancellationToken);

    internal static async Task EnsurePhysicalSchemaAsync(FileDbContext db, CancellationToken cancellationToken)
    {
        // Validate the entire required column/type/nullability shape, not just table existence.
        var ready = await db.Database.SqlQueryRaw<bool>("""
            SELECT NOT EXISTS (
                SELECT 1 FROM (VALUES
                    ('QuarantineUploadIntent','OperationId','uuid','NO'),
                    ('QuarantineUploadIntent','ParentOperationId','uuid','NO'),
                    ('QuarantineUploadIntent','Bucket','character varying','NO'),
                    ('QuarantineUploadIntent','ObjectName','character varying','NO'),
                    ('QuarantineUploadIntent','ContentType','character varying','NO'),
                    ('QuarantineUploadIntent','DeclaredSize','bigint','NO'),
                    ('QuarantineUploadIntent','AcknowledgedGeneration','bigint','YES'),
                    ('QuarantineUploadIntent','State','character varying','NO'),
                    ('QuarantineUploadIntent','CreatedAt','timestamp with time zone','NO'),
                    ('QuarantineUploadIntent','ModifiedAt','timestamp with time zone','NO'),
                    ('StorageMoveJournal','OperationId','uuid','NO'),
                    ('StorageMoveJournal','ScanClean','boolean','NO'),
                    ('StorageMoveJournal','SourceBucket','character varying','NO'),
                    ('StorageMoveJournal','SourceObjectName','character varying','NO'),
                    ('StorageMoveJournal','SourceGeneration','bigint','NO'),
                    ('StorageMoveJournal','DestinationBucket','character varying','NO'),
                    ('StorageMoveJournal','DestinationObjectName','character varying','NO'),
                    ('StorageMoveJournal','DestinationGeneration','bigint','YES'),
                    ('StorageMoveJournal','State','character varying','NO'),
                    ('StorageMoveJournal','CreatedAt','timestamp with time zone','NO'),
                    ('StorageMoveJournal','ModifiedAt','timestamp with time zone','NO')
                ) AS required(table_name,column_name,data_type,is_nullable)
                WHERE NOT EXISTS (SELECT 1 FROM information_schema.columns actual
                    WHERE actual.table_schema='public' AND actual.table_name=required.table_name
                      AND actual.column_name=required.column_name AND actual.data_type=required.data_type
                      AND actual.is_nullable=required.is_nullable)
            ) AND NOT EXISTS (
                SELECT 1 FROM (VALUES
                    ('QuarantineUploadIntent','PK_QuarantineUploadIntent','PRIMARY KEY'),
                    ('QuarantineUploadIntent','CK_QuarantineUploadIntent_Generation','CHECK'),
                    ('QuarantineUploadIntent','CK_QuarantineUploadIntent_DeclaredSize','CHECK'),
                    ('StorageMoveJournal','PK_StorageMoveJournal','PRIMARY KEY'),
                    ('StorageMoveJournal','CK_StorageMoveJournal_SourceGeneration','CHECK'),
                    ('StorageMoveJournal','CK_StorageMoveJournal_DestinationGeneration','CHECK')
                ) AS required(table_name,constraint_name,constraint_type)
                WHERE NOT EXISTS (SELECT 1 FROM information_schema.table_constraints actual
                    WHERE actual.table_schema='public' AND actual.table_name=required.table_name
                      AND actual.constraint_name=required.constraint_name AND actual.constraint_type=required.constraint_type)
            ) AND NOT EXISTS (
                SELECT 1 FROM (VALUES
                    ('QuarantineUploadIntent','Bucket',255),
                    ('QuarantineUploadIntent','ObjectName',1024),
                    ('QuarantineUploadIntent','ContentType',255),
                    ('QuarantineUploadIntent','State',32),
                    ('StorageMoveJournal','SourceBucket',255),
                    ('StorageMoveJournal','SourceObjectName',1024),
                    ('StorageMoveJournal','DestinationBucket',255),
                    ('StorageMoveJournal','DestinationObjectName',1024),
                    ('StorageMoveJournal','State',32)
                ) AS required(table_name,column_name,capacity)
                WHERE NOT EXISTS (SELECT 1 FROM information_schema.columns actual
                    WHERE actual.table_schema='public' AND actual.table_name=required.table_name
                      AND actual.column_name=required.column_name AND actual.character_maximum_length=required.capacity)
            ) AND NOT EXISTS (
                SELECT 1 FROM (VALUES
                    ('QuarantineUploadIntent','PK_QuarantineUploadIntent','PRIMARYKEY("OperationId")'),
                    ('QuarantineUploadIntent','CK_QuarantineUploadIntent_Generation','CHECK((("AcknowledgedGeneration"ISNULL)OR("AcknowledgedGeneration">0)))'),
                    ('QuarantineUploadIntent','CK_QuarantineUploadIntent_DeclaredSize','CHECK(("DeclaredSize">0))'),
                    ('StorageMoveJournal','PK_StorageMoveJournal','PRIMARYKEY("OperationId")'),
                    ('StorageMoveJournal','CK_StorageMoveJournal_SourceGeneration','CHECK(("SourceGeneration">0))'),
                    ('StorageMoveJournal','CK_StorageMoveJournal_DestinationGeneration','CHECK((("DestinationGeneration"ISNULL)OR("DestinationGeneration">0)))')
                ) AS required(table_name,constraint_name,definition)
                WHERE NOT EXISTS (SELECT 1 FROM pg_catalog.pg_constraint actual
                    JOIN pg_catalog.pg_class relation ON relation.oid=actual.conrelid
                    JOIN pg_catalog.pg_namespace space ON space.oid=relation.relnamespace
                    WHERE space.nspname='public' AND relation.relname=required.table_name
                      AND actual.conname=required.constraint_name AND actual.convalidated
                      AND regexp_replace(pg_get_constraintdef(actual.oid),'\s','','g')=required.definition)
            ) AS "Value"
            """).SingleAsync(cancellationToken);
        if (!ready) throw new UploadOutcomeUnknownException("Private upload recovery schema is unavailable.");
    }
}
