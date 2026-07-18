using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Legacy.Maliev.FileService.Data;

/// <summary>PostgreSQL persistence for instant-quotation ownership and workflow state.</summary>
public sealed class InstantQuoteFileRepository(FileDbContext dbContext) : IInstantQuoteFileRepository
{
    private const string UploadIdempotencyIndexName = "IX_InstantQuoteUploadFile_SessionId_IdempotencyKeyHash";
    private const string FinalizationIdempotencyIndexName = "IX_InstantQuoteFinalization_SessionId_IdempotencyKeyHash";

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
    public async Task<InstantQuoteReservation<InstantQuoteUploadFile>> ReserveUploadAsync(
        InstantQuoteUploadFile upload,
        CancellationToken cancellationToken)
    {
        dbContext.InstantQuoteUploadFiles.Add(upload);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return new(InstantQuoteReservationStatus.Acquired, upload, GetVersion(upload));
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception, UploadIdempotencyIndexName))
        {
            dbContext.Entry(upload).State = EntityState.Detached;
            var existing = await dbContext.InstantQuoteUploadFiles
                .AsNoTracking()
                .Where(value => value.SessionId == upload.SessionId && value.IdempotencyKeyHash == upload.IdempotencyKeyHash)
                .Select(value => new { Record = value, Version = EF.Property<uint>(value, "xmin") })
                .SingleAsync(cancellationToken);
            return new(Classify(existing.Record.RequestFingerprint, upload.RequestFingerprint, existing.Record.State),
                existing.Record, existing.Version);
        }
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
        await dbContext.SaveChangesAsync(cancellationToken);
        return GetVersion(upload);
    }

    /// <inheritdoc />
    public async Task<InstantQuoteReservation<InstantQuoteFinalization>> ReserveFinalizationAsync(
        InstantQuoteFinalization finalization,
        CancellationToken cancellationToken)
    {
        dbContext.InstantQuoteFinalizations.Add(finalization);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return new(InstantQuoteReservationStatus.Acquired, finalization, GetVersion(finalization));
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception, FinalizationIdempotencyIndexName))
        {
            dbContext.Entry(finalization).State = EntityState.Detached;
            var existing = await dbContext.InstantQuoteFinalizations
                .AsNoTracking()
                .Where(value => value.SessionId == finalization.SessionId
                    && value.IdempotencyKeyHash == finalization.IdempotencyKeyHash)
                .Select(value => new { Record = value, Version = EF.Property<uint>(value, "xmin") })
                .SingleAsync(cancellationToken);
            return new(Classify(existing.Record.RequestFingerprint, finalization.RequestFingerprint, existing.Record.State),
                existing.Record, existing.Version);
        }
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

    private static bool IsUniqueViolation(DbUpdateException exception, string constraintName) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: var actualConstraint,
        }
        && string.Equals(actualConstraint, constraintName, StringComparison.Ordinal);
}
