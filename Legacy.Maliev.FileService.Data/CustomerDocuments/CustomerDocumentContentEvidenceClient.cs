using Legacy.Maliev.FileService.Application.CustomerDocuments;
using Microsoft.EntityFrameworkCore;
namespace Legacy.Maliev.FileService.Data.CustomerDocuments;
/// <summary>Joins exact persisted registry content to current protected storage evidence.</summary>
public sealed class CustomerDocumentContentEvidenceClient(CustomerDocumentDbContext context,
    IProtectedDocumentStorage storage, CustomerDocumentRegistryOptions options) : ICustomerDocumentContentEvidence
{
    /// <inheritdoc />
    public async Task<DocumentAuthorityOutcome> ValidateAsync(Guid versionId, string contentSha256, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!options.Enabled || versionId == Guid.Empty || contentSha256 is null || contentSha256.Length != 64 ||
            contentSha256.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            return DocumentAuthorityOutcome.Unavailable;

        // Authority belongs to the registry caller. Coordinates come exclusively from its immutable
        // exact-version record, joined to the canonical parent customer and classification.
        var version = await (from v in context.Versions.AsNoTracking()
                             join d in context.Documents.AsNoTracking() on v.DocumentId equals d.Id
                             where v.Id == versionId && v.CustomerId == d.CustomerId && v.Kind == d.Kind
                                 && v.CustomerId > 0 && v.DocumentId != Guid.Empty && v.AssociationsSealed
                             select v).SingleOrDefaultAsync(token);
        if (version is null || !string.Equals(version.ContentSha256, contentSha256, StringComparison.Ordinal))
            return DocumentAuthorityOutcome.Unavailable;
        var expectedObject = FormattableString.Invariant(
            $"{CustomerDocumentOptions.ReservedPrefix}{version.CustomerId}/{version.DocumentId:N}/{version.Id:N}/original");
        if (!string.Equals(version.StorageObjectName, expectedObject, StringComparison.Ordinal))
            return DocumentAuthorityOutcome.Unavailable;

        var content = new DocumentStoredContent(version.StorageBucket, version.StorageObjectName,
            version.StorageGeneration, version.ScanSourceGeneration, version.ScanOperationId, version.ContentSize,
            version.ContentSha256, version.ContentType, version.OriginalFileName);
        try
        {
            // This reader requires committed clean journal evidence, a matching live generation,
            // and bounded fully buffered bytes whose exact size and SHA256 match the record.
            await storage.ReadAsync(content, token);
            return DocumentAuthorityOutcome.Allowed;
        }
        catch (DocumentAuthorityUnavailableException)
        {
            return DocumentAuthorityOutcome.Unavailable;
        }
    }
}
