using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Domain;
using Legacy.Maliev.FileService.Application.Services;
using Microsoft.EntityFrameworkCore;

namespace Legacy.Maliev.FileService.Data;

/// <summary>PostgreSQL-backed, non-expiring storage move evidence.</summary>
public sealed class StorageMoveJournalRepository(FileDbContext db, TimeProvider clock) : IStorageMoveJournal, IStorageReadJournal
{
    /// <inheritdoc />
    public Task<bool> TryBeginMetadataSubmissionAsync(IReadOnlyList<StorageMoveClaim> claims, CancellationToken cancellationToken) =>
        TransitionBatchAsync(claims, "SourceDeleted", "MetadataSubmitting", requireAbsence: true, cancellationToken);
    /// <inheritdoc />
    public Task<bool> TryBeginCompensationAsync(IReadOnlyList<StorageMoveClaim> claims, CancellationToken cancellationToken) =>
        TransitionBatchAsync(claims, "SourceDeleted", "CompensationPending", requireAbsence: true, cancellationToken);
    /// <inheritdoc />
    public async Task MetadataSubmissionCommittedAsync(IReadOnlyList<StorageMoveClaim> claims, CancellationToken cancellationToken)
    {
        if (!await TransitionBatchAsync(claims, "MetadataSubmitting", "MetadataCommitted", requireAbsence: false, cancellationToken))
            throw new UploadOutcomeUnknownException("Metadata recovery checkpoint changed.");
    }
    /// <inheritdoc />
    public async Task RecordCompensationAsync(StorageMoveClaim claim, CompensationDisposition disposition, CancellationToken cancellationToken)
    {
        ValidateClaims([claim]);
        var state = disposition switch
        {
            CompensationDisposition.Removed => "CompensatedRemoved",
            CompensationDisposition.Absent => "CompensatedAbsent",
            CompensationDisposition.Unknown => "CompensationUnknown",
            _ => throw new ArgumentOutOfRangeException(nameof(disposition)),
        };
        var evidence = claim.Evidence;
        var updated = await db.StorageMoveJournals.Where(row => row.OperationId == claim.OperationId
                && row.State == "CompensationPending" && row.ScanClean == evidence.ScanClean
                && row.SourceBucket == evidence.SourceBucket && row.SourceObjectName == evidence.SourceObjectName
                && row.SourceGeneration == evidence.SourceGeneration && row.DestinationBucket == evidence.DestinationBucket
                && row.DestinationObjectName == evidence.DestinationObjectName && row.DestinationGeneration == evidence.DestinationGeneration)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.State, state)
                .SetProperty(row => row.ModifiedAt, clock.GetUtcNow()), cancellationToken);
        RequireTransition(updated);
    }
    /// <inheritdoc />
    public async Task<StorageMoveEvidence?> FindCommittedSourceAsync(string bucket, string objectName, CancellationToken cancellationToken)
    {
        await QuarantineUploadIntentRepository.EnsurePhysicalSchemaAsync(db, cancellationToken);
        var rows = await db.StorageMoveJournals.AsNoTracking().Where(row => row.DestinationBucket == bucket
            && row.DestinationObjectName == objectName).Take(2).ToArrayAsync(cancellationToken);
        if (rows.Length != 1) return null;
        var row = rows[0];
        if (!row.ScanClean || row.State != "MetadataCommitted" || row.SourceGeneration <= 0 || row.DestinationGeneration is not > 0
            || string.IsNullOrWhiteSpace(row.SourceBucket) || string.IsNullOrWhiteSpace(row.SourceObjectName)) return null;
        return Evidence(row);
    }

    /// <inheritdoc />
    public async Task<StorageReadEvidence> FindReadEvidenceAsync(string bucket, string objectName, CancellationToken cancellationToken)
    {
        // Missing or incompatible physical schema is a failure, never historical absence.
        await QuarantineUploadIntentRepository.EnsurePhysicalSchemaAsync(db, cancellationToken);
        var rows = await db.StorageMoveJournals.AsNoTracking().Where(row => row.DestinationBucket == bucket
            && row.DestinationObjectName == objectName).Take(2).ToArrayAsync(cancellationToken);
        if (rows.Length == 0) return new(StorageReadState.Absent);
        if (rows.Length != 1) return new(StorageReadState.Ambiguous);
        var row = rows[0];
        if (row.State is "CompensatedRemoved" or "CompensatedAbsent") return new(StorageReadState.Revoked);
        if (!row.ScanClean) return new(StorageReadState.Unclean);
        if (row.State != "MetadataCommitted" || row.SourceGeneration <= 0 || row.DestinationGeneration is not > 0
            || string.IsNullOrWhiteSpace(row.SourceBucket) || string.IsNullOrWhiteSpace(row.SourceObjectName))
            return new(StorageReadState.Incomplete);
        return new(StorageReadState.Confirmed, Evidence(row));
    }

    private async Task<bool> TransitionBatchAsync(IReadOnlyList<StorageMoveClaim> claims, string expectedState,
        string targetState, bool requireAbsence, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ValidateClaims(claims);
        if (db.Database.CurrentTransaction is not null || System.Transactions.Transaction.Current is not null)
            throw new UploadOutcomeUnknownException("Storage recovery transaction is unavailable.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var attempted = false;
        // Enter the registered strategy so its queries permit this transaction, but never
        // replay a claim whose COMMIT or disposal acknowledgment may have been lost.
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            if (attempted) throw new UploadOutcomeUnknownException("Storage recovery transition requires reconciliation.");
            attempted = true;
            try
            {
                await QuarantineUploadIntentRepository.EnsurePhysicalSchemaAsync(db, deadline.Token);
                await using var transaction = await db.Database.BeginTransactionAsync(deadline.Token);
                var locked = new List<(StorageMoveClaim Claim, StorageMoveJournal Row)>();
                foreach (var claim in claims.OrderBy(claim => claim.OperationId))
                {
                    var rows = await db.StorageMoveJournals.FromSqlInterpolated($"SELECT * FROM \"StorageMoveJournal\" WHERE \"OperationId\"={claim.OperationId} FOR UPDATE")
                        .AsNoTracking().ToArrayAsync(deadline.Token);
                    if (rows.Length != 1) return false;
                    locked.Add((claim, rows[0]));
                }
                foreach (var (claim, row) in locked)
                {
                    if (row.State != expectedState || Evidence(row) with { State = "SourceDeleted" } != claim.Evidence) return false;
                    var exists = await db.Uploads.AsNoTracking().AnyAsync(upload => upload.Bucket == row.DestinationBucket
                        && upload.Name == row.DestinationObjectName, deadline.Token);
                    if (requireAbsence == exists) return false;
                }
                var now = clock.GetUtcNow();
                foreach (var (claim, _) in locked)
                {
                    var updated = await db.StorageMoveJournals.Where(row => row.OperationId == claim.OperationId && row.State == expectedState)
                        .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.State, targetState)
                            .SetProperty(row => row.ModifiedAt, now), deadline.Token);
                    RequireTransition(updated);
                }
                await transaction.CommitAsync(deadline.Token);
                return true;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                throw new UploadOutcomeUnknownException("Storage recovery transition requires reconciliation.", exception);
            }
        });
    }

    private static void ValidateClaims(IReadOnlyList<StorageMoveClaim> claims)
    {
        if (claims.Count == 0 || claims.Select(claim => claim.OperationId).Distinct().Count() != claims.Count
            || claims.Any(claim => claim.OperationId == Guid.Empty || claim.Evidence is null || !claim.Evidence.ScanClean
                || claim.Evidence.State != "SourceDeleted" || claim.Evidence.SourceGeneration <= 0 || claim.Evidence.DestinationGeneration is not > 0
                || string.IsNullOrWhiteSpace(claim.Evidence.SourceBucket) || string.IsNullOrWhiteSpace(claim.Evidence.SourceObjectName)
                || string.IsNullOrWhiteSpace(claim.Evidence.DestinationBucket) || string.IsNullOrWhiteSpace(claim.Evidence.DestinationObjectName)))
            throw new ArgumentException("Exact complete promotion evidence is required.", nameof(claims));
    }

    private static StorageMoveEvidence Evidence(StorageMoveJournal row) => new(row.ScanClean, row.SourceBucket, row.SourceObjectName,
        row.SourceGeneration, row.DestinationBucket, row.DestinationObjectName, row.DestinationGeneration, row.State);
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
        db.StorageMoveJournals.Where(move => move.OperationId == operationId
                && (move.State == "SourceObserved" || move.State == "Copied" || move.State == "SourceDeleted" || move.State == "Unknown"))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(move => move.State, "Unknown")
                .SetProperty(move => move.ModifiedAt, clock.GetUtcNow()), cancellationToken);

    private static void RequireTransition(int updated)
    {
        if (updated != 1) throw new InvalidOperationException("Storage move checkpoint changed; reconciliation is required.");
    }
}
