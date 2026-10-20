namespace Legacy.Maliev.FileService.Application.CustomerDocuments;

/// <summary>Supplies an authenticated server reservation and configured private expectations, never browser coordinates.</summary>
public sealed record DocumentQuarantineUploadIntentRequest(DocumentUploadReservation Reservation,
    string ExpectedBucket, string ExpectedContentType, long ExpectedDeclaredSize);

/// <summary>Retains read-only initial-upload custody; every state is independent of malware scan certification.</summary>
public sealed record DocumentQuarantineUploadIntentSnapshot(Guid OperationId, Guid ParentOperationId,
    string Bucket, string ObjectName, string ContentType, long DeclaredSize, long? AcknowledgedGeneration,
    string State, DateTimeOffset CreatedAtUtc, DateTimeOffset ModifiedAtUtc)
{
    /// <summary>Always returns false: upload intent custody cannot certify clean content.</summary>
    public bool IsCleanEvidence => false;
}

/// <summary>Reads exact existing File-owned custody without preparing, acknowledging, retrying or signing an upload.</summary>
public interface IDocumentQuarantineUploadIntentReader
{
    /// <summary>Returns retained custody or fails unavailable; no member, scan or retry authority is granted.</summary>
    Task<DocumentQuarantineUploadIntentSnapshot> ReadAsync(DocumentQuarantineUploadIntentRequest request, CancellationToken token);
}
