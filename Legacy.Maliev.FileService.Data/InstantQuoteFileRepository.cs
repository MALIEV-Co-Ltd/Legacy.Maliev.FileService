using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Models;
using Legacy.Maliev.FileService.Domain;
using Microsoft.EntityFrameworkCore;

namespace Legacy.Maliev.FileService.Data;

/// <summary>PostgreSQL persistence for instant-quotation ownership and workflow state.</summary>
public sealed class InstantQuoteFileRepository(FileDbContext dbContext) : IInstantQuoteFileRepository, IInstantQuoteCleanupRepository
{
    /// <inheritdoc />
    public async Task CreateSessionAsync(InstantQuoteUploadSession session, CancellationToken cancellationToken)
    {
        dbContext.InstantQuoteUploadSessions.Add(session);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<InstantQuoteUploadSession?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken) =>
        dbContext.InstantQuoteUploadSessions.AsNoTracking().SingleOrDefaultAsync(value => value.Id == sessionId, cancellationToken);

    /// <inheritdoc />
    public async Task<InstantQuoteUploadSession?> VerifySessionAsync(Guid sessionId, byte[] tokenHash, string? ownerSubject,
        bool isAuthenticated, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var session = await GetSessionAsync(sessionId, cancellationToken);
        return session is not null && session.VerifyOwnership(tokenHash, ownerSubject, isAuthenticated, now)
            ? session
            : null;
    }

    /// <inheritdoc />
    public Task<InstantQuoteReservation<InstantQuoteUploadFile>> ReserveUploadAsync(
        InstantQuoteUploadFile upload,
        CancellationToken cancellationToken) => ReserveUploadCoreAsync(
            upload, upload.ModifiedAt, TimeSpan.Zero, allowRecovery: false, cancellationToken);

    /// <inheritdoc />
    public Task<InstantQuoteReservation<InstantQuoteUploadFile>> ReserveUploadAsync(
        InstantQuoteUploadFile upload,
        DateTimeOffset now,
        TimeSpan operationLeaseTimeout,
        CancellationToken cancellationToken) => ReserveUploadCoreAsync(
            upload, now, operationLeaseTimeout, allowRecovery: true, cancellationToken);

    private async Task<InstantQuoteReservation<InstantQuoteUploadFile>> ReserveUploadCoreAsync(
        InstantQuoteUploadFile upload,
        DateTimeOffset now,
        TimeSpan operationLeaseTimeout,
        bool allowRecovery,
        CancellationToken cancellationToken)
    {
        if (allowRecovery)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(operationLeaseTimeout, TimeSpan.Zero);
        }
        else
        {
            operationLeaseTimeout = TimeSpan.FromTicks(1);
        }
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({upload.SessionId.ToString("N")}, 0));",
            cancellationToken);

        var existingMatch = await dbContext.InstantQuoteUploadFiles
            .AsNoTracking()
            .Where(value => value.SessionId == upload.SessionId && value.IdempotencyKeyHash == upload.IdempotencyKeyHash)
            .Select(value => new { Record = value, Version = EF.Property<uint>(value, "xmin") })
            .SingleOrDefaultAsync(cancellationToken);
        if (existingMatch is not null)
        {
            var status = Classify(existingMatch.Record.RequestFingerprint, upload.RequestFingerprint, existingMatch.Record.State);
            var recoverable = string.Equals(
                    existingMatch.Record.RequestFingerprint,
                    upload.RequestFingerprint,
                    StringComparison.Ordinal) &&
                existingMatch.Record.State is InstantQuoteWorkflowState.Pending or
                    InstantQuoteWorkflowState.Uploaded or InstantQuoteWorkflowState.Unknown;
            if (allowRecovery && recoverable &&
                existingMatch.Record.ModifiedAt <= now.Subtract(operationLeaseTimeout))
            {
                existingMatch.Record.ModifiedAt = now;
                var claimedVersion = await SaveUploadAsync(existingMatch.Record, existingMatch.Version, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new(InstantQuoteReservationStatus.Recovered, existingMatch.Record, claimedVersion);
            }
            if (allowRecovery && recoverable)
            {
                status = InstantQuoteReservationStatus.InProgress;
            }
            await transaction.CommitAsync(cancellationToken);
            return new(status, existingMatch.Record, existingMatch.Version);
        }

        var fileCount = await dbContext.InstantQuoteUploadFiles
            .CountAsync(value => value.SessionId == upload.SessionId, cancellationToken);
        if (fileCount >= InstantQuoteFileContract.MaximumFilesPerSession)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(InstantQuoteReservationStatus.LimitExceeded, upload, 0);
        }

        dbContext.InstantQuoteUploadFiles.Add(upload);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(InstantQuoteReservationStatus.Acquired, upload, GetVersion(upload));
    }

    /// <inheritdoc />
    public async Task<uint> SaveUploadAsync(
        InstantQuoteUploadFile upload,
        uint expectedVersion,
        CancellationToken cancellationToken)
    {
        var tracked = dbContext.InstantQuoteUploadFiles.Local.SingleOrDefault(value => value.Id == upload.Id);
        if (tracked is not null && !ReferenceEquals(tracked, upload))
        {
            dbContext.Entry(tracked).State = EntityState.Detached;
        }

        var entry = dbContext.Entry(upload);
        if (entry.State == EntityState.Detached)
        {
            dbContext.Attach(upload);
            entry.State = EntityState.Modified;
        }

        entry.Property<uint>("xmin").OriginalValue = expectedVersion;
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            entry.State = EntityState.Detached;
            throw new InstantQuoteConcurrencyException("The upload state was changed concurrently.", exception);
        }
        return GetVersion(upload);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InstantQuoteStoredUpload>> GetSessionFilesAsync(
        Guid sessionId,
        IReadOnlyCollection<Guid> fileIds,
        CancellationToken cancellationToken)
    {
        var requested = fileIds.Distinct().ToArray();
        var stored = await dbContext.InstantQuoteUploadFiles
            .AsNoTracking()
            .Where(value => value.SessionId == sessionId && requested.Contains(value.Id))
            .Select(value => new
            {
                Upload = value,
                Version = EF.Property<uint>(value, "xmin"),
            })
            .ToListAsync(cancellationToken);
        return stored.Select(value => new InstantQuoteStoredUpload(value.Upload, value.Version)).ToArray();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InstantQuoteStoredUpload>> GetTemporaryCleanupCandidatesAsync(
        DateTimeOffset expiredBefore,
        DateTimeOffset retryBefore,
        int batchSize,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        var candidates = await (
                from upload in dbContext.InstantQuoteUploadFiles.AsNoTracking()
                join session in dbContext.InstantQuoteUploadSessions.AsNoTracking()
                    on upload.SessionId equals session.Id
                where !upload.TemporaryCleanupCompleted && upload.ModifiedAt <= retryBefore &&
                    (upload.State == InstantQuoteWorkflowState.Pending ||
                     upload.State == InstantQuoteWorkflowState.Uploaded ||
                     upload.State == InstantQuoteWorkflowState.Unknown ||
                     upload.State == InstantQuoteWorkflowState.PayloadTooLarge ||
                     upload.State == InstantQuoteWorkflowState.InvalidRequest ||
                     upload.GcsGeneration != null &&
                    (upload.State == InstantQuoteWorkflowState.Finalized ||
                     upload.State == InstantQuoteWorkflowState.Failed ||
                     upload.State == InstantQuoteWorkflowState.PayloadTooLarge ||
                     upload.State == InstantQuoteWorkflowState.InvalidRequest ||
                     upload.State == InstantQuoteWorkflowState.Removed ||
                     upload.State == InstantQuoteWorkflowState.Clean && session.ExpiresAt <= expiredBefore))
                orderby session.ExpiresAt, upload.ModifiedAt, upload.Id
                select new
                {
                    Upload = upload,
                    Version = EF.Property<uint>(upload, "xmin"),
                })
            .Take(batchSize)
            .ToListAsync(cancellationToken);
        return candidates.Select(value => new InstantQuoteStoredUpload(value.Upload, value.Version)).ToArray();
    }

    /// <inheritdoc />
    public Task<uint> SaveCleanupStateAsync(
        InstantQuoteUploadFile upload,
        uint expectedVersion,
        CancellationToken cancellationToken) => SaveUploadAsync(upload, expectedVersion, cancellationToken);

    /// <inheritdoc />
    public Task<InstantQuoteReservation<InstantQuoteFinalization>> ReserveFinalizationAsync(
        InstantQuoteFinalization finalization,
        CancellationToken cancellationToken) => ReserveFinalizationCoreAsync(
            finalization, finalization.ModifiedAt, TimeSpan.Zero, allowRecovery: false, cancellationToken);

    /// <inheritdoc />
    public Task<InstantQuoteReservation<InstantQuoteFinalization>> ReserveFinalizationAsync(
        InstantQuoteFinalization finalization,
        DateTimeOffset now,
        TimeSpan operationLeaseTimeout,
        CancellationToken cancellationToken) => ReserveFinalizationCoreAsync(
            finalization, now, operationLeaseTimeout, allowRecovery: true, cancellationToken);

    private async Task<InstantQuoteReservation<InstantQuoteFinalization>> ReserveFinalizationCoreAsync(
        InstantQuoteFinalization finalization,
        DateTimeOffset now,
        TimeSpan operationLeaseTimeout,
        bool allowRecovery,
        CancellationToken cancellationToken)
    {
        if (allowRecovery)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(operationLeaseTimeout, TimeSpan.Zero);
        }
        else
        {
            operationLeaseTimeout = TimeSpan.FromTicks(1);
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({finalization.SessionId.ToString("N")}, 0));",
            cancellationToken);
        var existing = await dbContext.InstantQuoteFinalizations
            .AsNoTracking()
            .Where(value => value.SessionId == finalization.SessionId
                && value.IdempotencyKeyHash == finalization.IdempotencyKeyHash)
            .Select(value => new { Record = value, Version = EF.Property<uint>(value, "xmin") })
            .SingleOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            var status = Classify(existing.Record.RequestFingerprint, finalization.RequestFingerprint, existing.Record.State);
            var recoverable = string.Equals(
                    existing.Record.RequestFingerprint,
                    finalization.RequestFingerprint,
                    StringComparison.Ordinal) &&
                existing.Record.State is InstantQuoteWorkflowState.Pending or InstantQuoteWorkflowState.Unknown;
            if (allowRecovery && recoverable &&
                existing.Record.ModifiedAt <= now.Subtract(operationLeaseTimeout))
            {
                existing.Record.ModifiedAt = now;
                var claimedVersion = await SaveFinalizationAsync(existing.Record, existing.Version, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new(InstantQuoteReservationStatus.Recovered, existing.Record, claimedVersion);
            }
            if (allowRecovery && recoverable)
            {
                status = InstantQuoteReservationStatus.InProgress;
            }
            await transaction.CommitAsync(cancellationToken);
            return new(status, existing.Record, existing.Version);
        }

        dbContext.InstantQuoteFinalizations.Add(finalization);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(InstantQuoteReservationStatus.Acquired, finalization, GetVersion(finalization));
    }

    /// <inheritdoc />
    public async Task<uint> SaveFinalizationAsync(
        InstantQuoteFinalization finalization,
        uint expectedVersion,
        CancellationToken cancellationToken)
    {
        var tracked = dbContext.InstantQuoteFinalizations.Local.SingleOrDefault(value => value.Id == finalization.Id);
        if (tracked is not null && !ReferenceEquals(tracked, finalization))
        {
            dbContext.Entry(tracked).State = EntityState.Detached;
        }

        var entry = dbContext.Entry(finalization);
        if (entry.State == EntityState.Detached)
        {
            dbContext.Attach(finalization);
            entry.State = EntityState.Modified;
        }

        entry.Property<uint>("xmin").OriginalValue = expectedVersion;
        await dbContext.SaveChangesAsync(cancellationToken);
        return GetVersion(finalization);
    }

    private static InstantQuoteReservationStatus Classify(
        string existingFingerprint,
        string requestedFingerprint,
        InstantQuoteWorkflowState state)
    {
        if (!string.Equals(existingFingerprint, requestedFingerprint, StringComparison.Ordinal))
        {
            return InstantQuoteReservationStatus.Conflict;
        }

        return state switch
        {
            InstantQuoteWorkflowState.Pending => InstantQuoteReservationStatus.InProgress,
            InstantQuoteWorkflowState.Unknown => InstantQuoteReservationStatus.Unknown,
            _ => InstantQuoteReservationStatus.Replay,
        };
    }

    private uint GetVersion<TEntity>(TEntity entity) where TEntity : class =>
        dbContext.Entry(entity).Property<uint>("xmin").CurrentValue;

}
