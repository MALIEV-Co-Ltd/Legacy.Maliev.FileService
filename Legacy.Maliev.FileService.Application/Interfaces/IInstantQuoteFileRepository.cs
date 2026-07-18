using Legacy.Maliev.FileService.Domain;

namespace Legacy.Maliev.FileService.Application.Interfaces;

/// <summary>Classifies an atomic instant-quotation reservation attempt.</summary>
public enum InstantQuoteReservationStatus
{
    /// <summary>The caller owns a newly created reservation.</summary>
    Acquired,
    /// <summary>A completed matching reservation can be replayed.</summary>
    Replay,
    /// <summary>The key was previously used for a different request.</summary>
    Conflict,
    /// <summary>A matching reservation is still being processed.</summary>
    InProgress,
    /// <summary>A matching reservation has an ambiguous outcome.</summary>
    Unknown,
}

/// <summary>Result of atomically reserving an idempotency key.</summary>
/// <typeparam name="TRecord">Persisted workflow record type.</typeparam>
/// <param name="Status">Reservation classification.</param>
/// <param name="Record">Authoritative persisted record.</param>
/// <param name="Version">Caller-observed PostgreSQL xmin value required for a state transition.</param>
public sealed record InstantQuoteReservation<TRecord>(InstantQuoteReservationStatus Status, TRecord Record, uint Version);

/// <summary>An exact session-owned upload and its observed PostgreSQL xmin.</summary>
public sealed record InstantQuoteStoredUpload(InstantQuoteUploadFile Upload, uint Version);

/// <summary>Persistence boundary dedicated to the instant-quotation file workflow.</summary>
public interface IInstantQuoteFileRepository
{
    /// <summary>Creates a durable upload session.</summary>
    Task CreateSessionAsync(InstantQuoteUploadSession session, CancellationToken cancellationToken);
    /// <summary>Loads a session by opaque identifier.</summary>
    Task<InstantQuoteUploadSession?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken);
    /// <summary>Verifies a session capability, expiry, and owner binding without distinguishing rejection reasons.</summary>
    Task<InstantQuoteUploadSession?> VerifySessionAsync(Guid sessionId, byte[] tokenHash, string? ownerSubject,
        bool isAuthenticated, DateTimeOffset now, CancellationToken cancellationToken);
    /// <summary>Atomically reserves or classifies an upload idempotency key.</summary>
    Task<InstantQuoteReservation<InstantQuoteUploadFile>> ReserveUploadAsync(InstantQuoteUploadFile upload, CancellationToken cancellationToken);
    /// <summary>Persists authoritative upload metadata and state.</summary>
    Task<uint> SaveUploadAsync(InstantQuoteUploadFile upload, uint expectedVersion, CancellationToken cancellationToken);
    /// <summary>Loads exactly the requested file IDs when they all belong to the session.</summary>
    Task<IReadOnlyList<InstantQuoteStoredUpload>> GetSessionFilesAsync(
        Guid sessionId,
        IReadOnlyCollection<Guid> fileIds,
        CancellationToken cancellationToken);
    /// <summary>Atomically reserves or classifies a finalization idempotency key.</summary>
    Task<InstantQuoteReservation<InstantQuoteFinalization>> ReserveFinalizationAsync(InstantQuoteFinalization finalization, CancellationToken cancellationToken);
    /// <summary>Persists authoritative finalization state.</summary>
    Task<uint> SaveFinalizationAsync(InstantQuoteFinalization finalization, uint expectedVersion, CancellationToken cancellationToken);
}
