using System.Globalization;
using Legacy.Maliev.FileService.Application.CustomerDocuments;
using Legacy.Maliev.FileService.Application.Services;
using Legacy.Maliev.FileService.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Legacy.Maliev.FileService.Data.CustomerDocuments;

/// <summary>Reads existing initial-upload custody without certifying clean bytes or modifying owner records.</summary>
public sealed class DocumentQuarantineUploadIntentReader(FileDbContext? db = null, bool enabled = false,
    TimeProvider? timeProvider = null) : IDocumentQuarantineUploadIntentReader
{
    /// <inheritdoc />
    public async Task<DocumentQuarantineUploadIntentSnapshot> ReadAsync(DocumentQuarantineUploadIntentRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!enabled || db is null || !ValidRequest(request))
            throw new DocumentAuthorityUnavailableException();

        // Only provider initialization can be sanitized here; reader logic errors
        // are not swallowed by a broad InvalidOperationException catch.
        try
        {
            if (!db.Database.IsNpgsql()) throw new DocumentAuthorityUnavailableException();
        }
        catch (InvalidOperationException)
        {
            token.ThrowIfCancellationRequested();
            throw new DocumentAuthorityUnavailableException();
        }

        var reservation = request.Reservation;
        var expectedName = CustomerDocumentOptions.ReservedPrefix + reservation.CustomerId.ToString(CultureInfo.InvariantCulture)
            + "/" + reservation.DocumentId.ToString("N") + "/" + reservation.VersionId.ToString("N")
            + "/quarantine/" + reservation.OperationId.ToString("N");
        try
        {
            // Reuse the real File-owned physical check, including validated constraints and StorageMoveJournal shape.
            // This method performs catalog reads only; it never migrates or repairs the target schema.
            await QuarantineUploadIntentRepository.EnsurePhysicalSchemaAsync(db, token);
            var row = await db.QuarantineUploadIntents.AsNoTracking()
                .SingleOrDefaultAsync(value => value.OperationId == reservation.OperationId, token);
            token.ThrowIfCancellationRequested();

            var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
            if (!ValidRow(row, request, expectedName, now)) throw new DocumentAuthorityUnavailableException();
            return new(row!.OperationId, row.ParentOperationId, row.Bucket, row.ObjectName, row.ContentType,
                row.DeclaredSize, row.AcknowledgedGeneration, row.State, row.CreatedAt, row.ModifiedAt);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (IsUnavailableDependency(error))
        {
            token.ThrowIfCancellationRequested();
            // Do not return SQL, provider, schema, or private coordinate details to a caller.
            throw new DocumentAuthorityUnavailableException();
        }
    }

    private static bool ValidRequest(DocumentQuarantineUploadIntentRequest? request)
    {
        if (request?.Reservation is not { } reservation || reservation.OperationId == Guid.Empty ||
            reservation.DocumentId == Guid.Empty || reservation.VersionId == Guid.Empty || reservation.CustomerId <= 0 ||
            reservation.VersionNumber <= 0 || reservation.Fingerprint is null || reservation.Fingerprint.Length != 64 ||
            reservation.Fingerprint.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            return false;
        return !string.IsNullOrWhiteSpace(request.ExpectedBucket) && request.ExpectedBucket.Length <= 255 &&
            request.ExpectedBucket == request.ExpectedBucket.Trim() && !request.ExpectedBucket.Any(char.IsControl) &&
            request.ExpectedContentType is "application/pdf" or "image/png" or "image/jpeg" &&
            request.ExpectedDeclaredSize is > 0 and <= CustomerDocumentOptions.MaximumBytes;
    }

    private static bool ValidRow(QuarantineUploadIntent? row, DocumentQuarantineUploadIntentRequest request,
        string expectedName, DateTimeOffset now)
    {
        if (row is null || row.OperationId != request.Reservation.OperationId || row.OperationId == Guid.Empty ||
            row.ParentOperationId != request.Reservation.OperationId ||
            !string.Equals(row.Bucket, request.ExpectedBucket, StringComparison.Ordinal) ||
            !string.Equals(row.ObjectName, expectedName, StringComparison.Ordinal) ||
            !string.Equals(row.ContentType, request.ExpectedContentType, StringComparison.Ordinal) ||
            row.DeclaredSize != request.ExpectedDeclaredSize ||
            row.AcknowledgedGeneration is <= 0 ||
            now == default || now == DateTimeOffset.MaxValue || now.Offset != TimeSpan.Zero ||
            row.CreatedAt == default || row.ModifiedAt == default ||
            row.CreatedAt == DateTimeOffset.MaxValue || row.ModifiedAt == DateTimeOffset.MaxValue ||
            row.CreatedAt.Offset != TimeSpan.Zero || row.ModifiedAt.Offset != TimeSpan.Zero ||
            row.ModifiedAt < row.CreatedAt || row.ModifiedAt > now)
            return false;

        // This whitelist is a feature-owned refusal policy based on the observed writer, pending exact owner ACK.
        // Uploaded acknowledges generation only. Pending/Unknown never authorize retry or infer scan certification.
        return row.State switch
        {
            "Pending" => row.AcknowledgedGeneration is null,
            "Uploaded" => row.AcknowledgedGeneration is > 0,
            "Unknown" => row.AcknowledgedGeneration is null or > 0,
            _ => false,
        };
    }

    private static bool IsUnavailableDependency(Exception error) =>
        error is NpgsqlException or UploadOutcomeUnknownException or IOException or TimeoutException or OperationCanceledException ||
        error.InnerException is not null && IsUnavailableDependency(error.InnerException);
}
