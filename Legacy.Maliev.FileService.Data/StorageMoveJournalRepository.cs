using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Domain;
using Microsoft.EntityFrameworkCore;

namespace Legacy.Maliev.FileService.Data;

/// <summary>PostgreSQL-backed, non-expiring storage move evidence.</summary>
public sealed class StorageMoveJournalRepository(FileDbContext db, TimeProvider clock) : IStorageMoveJournal
{
    /// <inheritdoc />
    public Task<StorageMoveEvidence?> FindAsync(Guid operationId, CancellationToken cancellationToken) =>
        db.StorageMoveJournals.AsNoTracking().Where(move => move.OperationId == operationId)
            .Select(move => new StorageMoveEvidence(move.ScanClean, move.SourceBucket, move.SourceObjectName,
                move.SourceGeneration, move.DestinationBucket, move.DestinationObjectName,
                move.DestinationGeneration, move.State))
            .SingleOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<bool> BeginAsync(Guid operationId, bool scanClean, string sourceBucket, string sourceObjectName,
        long sourceGeneration, string destinationBucket, string destinationObjectName, CancellationToken cancellationToken)
    {
        if (sourceGeneration <= 0) throw new ArgumentOutOfRangeException(nameof(sourceGeneration));
        var now = clock.GetUtcNow();
        const string state = "SourceObserved";
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "StorageMoveJournal" ("OperationId", "ScanClean", "SourceBucket", "SourceObjectName",
                "SourceGeneration", "DestinationBucket", "DestinationObjectName", "State", "CreatedAt", "ModifiedAt")
            VALUES ({operationId}, {scanClean}, {sourceBucket}, {sourceObjectName}, {sourceGeneration},
                {destinationBucket}, {destinationObjectName}, {state}, {now}, {now})
            ON CONFLICT ("OperationId") DO NOTHING
            """, cancellationToken);
        return inserted == 1;
    }

    /// <inheritdoc />
    public async Task CopiedAsync(Guid operationId, long destinationGeneration, CancellationToken cancellationToken)
    {
        if (destinationGeneration <= 0) throw new ArgumentOutOfRangeException(nameof(destinationGeneration));
        var updated = await db.StorageMoveJournals.Where(move => move.OperationId == operationId && move.State == "SourceObserved")
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(move => move.DestinationGeneration, destinationGeneration)
                .SetProperty(move => move.State, "Copied")
                .SetProperty(move => move.ModifiedAt, clock.GetUtcNow()), cancellationToken);
        RequireTransition(updated);
    }

    /// <inheritdoc />
    public async Task SourceDeletedAsync(Guid operationId, CancellationToken cancellationToken)
    {
        var updated = await db.StorageMoveJournals.Where(move => move.OperationId == operationId && move.State == "Copied")
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(move => move.State, "SourceDeleted")
                .SetProperty(move => move.ModifiedAt, clock.GetUtcNow()), cancellationToken);
        RequireTransition(updated);
    }

    /// <inheritdoc />
    public async Task MetadataCommittedAsync(Guid operationId, CancellationToken cancellationToken)
    {
        var updated = await db.StorageMoveJournals.Where(move => move.OperationId == operationId && move.State == "SourceDeleted")
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(move => move.State, "MetadataCommitted")
                .SetProperty(move => move.ModifiedAt, clock.GetUtcNow()), cancellationToken);
        RequireTransition(updated);
    }

    /// <inheritdoc />
    public Task UnknownAsync(Guid operationId, CancellationToken cancellationToken) =>
        db.StorageMoveJournals.Where(move => move.OperationId == operationId && move.State != "MetadataCommitted")
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(move => move.State, "Unknown")
                .SetProperty(move => move.ModifiedAt, clock.GetUtcNow()), cancellationToken);

    private static void RequireTransition(int updated)
    {
        if (updated != 1) throw new InvalidOperationException("Storage move checkpoint changed; reconciliation is required.");
    }
}
