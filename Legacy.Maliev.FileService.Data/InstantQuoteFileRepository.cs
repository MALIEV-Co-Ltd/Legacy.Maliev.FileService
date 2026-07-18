using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Legacy.Maliev.FileService.Data;

/// <summary>PostgreSQL persistence for instant-quotation ownership and workflow state.</summary>
public sealed class InstantQuoteFileRepository(FileDbContext dbContext) : IInstantQuoteFileRepository
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
    public async Task<InstantQuoteReservation<InstantQuoteUploadFile>> ReserveUploadAsync(
        InstantQuoteUploadFile upload,
        CancellationToken cancellationToken)
    {
        dbContext.InstantQuoteUploadFiles.Add(upload);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return new(InstantQuoteReservationStatus.Acquired, upload);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            dbContext.Entry(upload).State = EntityState.Detached;
            var existing = await dbContext.InstantQuoteUploadFiles.AsNoTracking().SingleAsync(
                value => value.SessionId == upload.SessionId && value.IdempotencyKeyHash == upload.IdempotencyKeyHash,
                cancellationToken);
            return new(Classify(existing.RequestFingerprint, upload.RequestFingerprint, existing.State), existing);
        }
    }

    /// <inheritdoc />
    public async Task SaveUploadAsync(InstantQuoteUploadFile upload, CancellationToken cancellationToken)
    {
        var tracked = dbContext.InstantQuoteUploadFiles.Local.SingleOrDefault(value => value.Id == upload.Id);
        if (tracked is null)
        {
            tracked = await dbContext.InstantQuoteUploadFiles.SingleAsync(value => value.Id == upload.Id, cancellationToken);
        }

        if (!ReferenceEquals(tracked, upload))
        {
            tracked.ActualSha256 = upload.ActualSha256;
            tracked.ActualSizeBytes = upload.ActualSizeBytes;
            tracked.GcsGeneration = upload.GcsGeneration;
            tracked.FinalObjectName = upload.FinalObjectName;
            tracked.State = upload.State;
            tracked.ModifiedAt = upload.ModifiedAt;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
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
            return new(InstantQuoteReservationStatus.Acquired, finalization);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            dbContext.Entry(finalization).State = EntityState.Detached;
            var existing = await dbContext.InstantQuoteFinalizations.AsNoTracking().SingleAsync(
                value => value.SessionId == finalization.SessionId
                    && value.IdempotencyKeyHash == finalization.IdempotencyKeyHash,
                cancellationToken);
            return new(Classify(existing.RequestFingerprint, finalization.RequestFingerprint, existing.State), existing);
        }
    }

    /// <inheritdoc />
    public async Task SaveFinalizationAsync(InstantQuoteFinalization finalization, CancellationToken cancellationToken)
    {
        var tracked = dbContext.InstantQuoteFinalizations.Local.SingleOrDefault(value => value.Id == finalization.Id);
        if (tracked is null)
        {
            tracked = await dbContext.InstantQuoteFinalizations.SingleAsync(value => value.Id == finalization.Id, cancellationToken);
        }

        if (!ReferenceEquals(tracked, finalization))
        {
            tracked.State = finalization.State;
            tracked.ModifiedAt = finalization.ModifiedAt;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
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

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
