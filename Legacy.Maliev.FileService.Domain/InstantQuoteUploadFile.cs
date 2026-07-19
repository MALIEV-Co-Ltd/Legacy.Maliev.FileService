namespace Legacy.Maliev.FileService.Domain;

/// <summary>State persisted for one instant-quotation upload reservation.</summary>
public sealed class InstantQuoteUploadFile
{
    private string? actualSha256;
    private int? finalizedQuotationRequestId;

    private InstantQuoteUploadFile()
    {
    }

    /// <summary>Creates an upload reservation.</summary>
    public InstantQuoteUploadFile(Guid id, Guid sessionId, byte[] idempotencyKeyHash, string requestFingerprint,
        string originalFileName, string validatedExtension, string validatedContentType, string expectedSha256,
        string? actualSha256, long? actualSizeBytes, long? gcsGeneration, string temporaryBucket,
        string temporaryObjectName, string? finalBucket, string? finalObjectName, InstantQuoteWorkflowState state,
        DateTimeOffset createdAt, DateTimeOffset modifiedAt)
    {
        ValidateHash(idempotencyKeyHash, nameof(idempotencyKeyHash));
        ValidateFingerprint(requestFingerprint, nameof(requestFingerprint));
        ValidateSha256(expectedSha256, nameof(expectedSha256));
        if (actualSha256 is not null)
        {
            ValidateSha256(actualSha256, nameof(actualSha256));
        }
        Id = id;
        SessionId = sessionId;
        IdempotencyKeyHash = idempotencyKeyHash.ToArray();
        RequestFingerprint = requestFingerprint;
        OriginalFileName = originalFileName;
        ValidatedExtension = validatedExtension;
        ValidatedContentType = validatedContentType;
        ExpectedSha256 = expectedSha256;
        ActualSha256 = actualSha256;
        ActualSizeBytes = actualSizeBytes;
        GcsGeneration = gcsGeneration;
        TemporaryBucket = temporaryBucket;
        TemporaryObjectName = temporaryObjectName;
        FinalBucket = finalBucket;
        FinalObjectName = finalObjectName;
        State = state;
        CreatedAt = createdAt;
        ModifiedAt = modifiedAt;
    }

    /// <summary>Gets the opaque file identifier.</summary>
    public Guid Id { get; private set; }
    /// <summary>Gets the owning session identifier.</summary>
    public Guid SessionId { get; private set; }
    /// <summary>Gets the SHA-256 idempotency-key hash.</summary>
    public byte[] IdempotencyKeyHash { get; private set; } = [];
    /// <summary>Gets the lowercase SHA-256 request fingerprint.</summary>
    public string RequestFingerprint { get; private set; } = string.Empty;
    /// <summary>Gets the customer filename retained only as metadata.</summary>
    public string OriginalFileName { get; private set; } = string.Empty;
    /// <summary>Gets the validated extension.</summary>
    public string ValidatedExtension { get; private set; } = string.Empty;
    /// <summary>Gets the validated content type.</summary>
    public string ValidatedContentType { get; private set; } = string.Empty;
    /// <summary>Gets the expected lowercase SHA-256 checksum.</summary>
    public string ExpectedSha256 { get; private set; } = string.Empty;
    /// <summary>Gets or sets the authoritative lowercase SHA-256 checksum.</summary>
    public string? ActualSha256
    {
        get => actualSha256;
        set
        {
            if (value is not null)
            {
                ValidateSha256(value, nameof(value));
            }

            actualSha256 = value;
        }
    }
    /// <summary>Gets or sets the authoritative object size.</summary>
    public long? ActualSizeBytes { get; set; }
    /// <summary>Gets or sets the authoritative GCS generation.</summary>
    public long? GcsGeneration { get; set; }
    /// <summary>Gets the durable private bucket containing the temporary generation.</summary>
    public string TemporaryBucket { get; private set; } = string.Empty;
    /// <summary>Gets the opaque temporary object name.</summary>
    public string TemporaryObjectName { get; private set; } = string.Empty;
    /// <summary>Gets or sets the durable private bucket containing the final object.</summary>
    public string? FinalBucket { get; set; }
    /// <summary>Gets or sets the opaque final object name.</summary>
    public string? FinalObjectName { get; set; }
    /// <summary>Gets or sets the quotation request that authoritatively owns the finalized object.</summary>
    public int? FinalizedQuotationRequestId
    {
        get => finalizedQuotationRequestId;
        set
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Quotation request identifiers must be positive.");
            }
            finalizedQuotationRequestId = value;
        }
    }
    /// <summary>Gets or sets the workflow state.</summary>
    public InstantQuoteWorkflowState State { get; set; }
    /// <summary>Gets the creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; private set; }
    /// <summary>Gets or sets the modification timestamp.</summary>
    public DateTimeOffset ModifiedAt { get; set; }

    private static void ValidateHash(byte[] hash, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(hash);
        if (hash.Length != 32) throw new ArgumentException("Idempotency hashes must be SHA-256 values.", parameterName);
    }

    private static void ValidateFingerprint(string fingerprint, string parameterName)
    {
        if (fingerprint.Length != 64 || fingerprint.Any(value => !char.IsAsciiHexDigit(value) || char.IsUpper(value)))
            throw new ArgumentException("Fingerprints must be 64 lowercase hexadecimal characters.", parameterName);
    }

    private static void ValidateSha256(string checksum, string parameterName)
    {
        if (checksum.Length != 64 || checksum.Any(value => !char.IsAsciiHexDigit(value) || char.IsUpper(value)))
            throw new ArgumentException("SHA-256 checksums must be 64 lowercase hexadecimal characters.", parameterName);
    }
}

/// <summary>States shared by instant-quotation upload and finalization workflows.</summary>
public enum InstantQuoteWorkflowState
{
    /// <summary>Reservation exists and work is in progress.</summary>
    Pending,
    /// <summary>Bytes were uploaded.</summary>
    Uploaded,
    /// <summary>Malware scanning accepted the object.</summary>
    Clean,
    /// <summary>The record is linked to a quotation request.</summary>
    Finalized,
    /// <summary>The workflow failed definitively.</summary>
    Failed,
    /// <summary>The outcome cannot be determined safely.</summary>
    Unknown,
    /// <summary>The pre-finalization file was explicitly removed.</summary>
    Removed,
    /// <summary>The streamed request exceeded the byte limit.</summary>
    PayloadTooLarge,
    /// <summary>The streamed request failed terminal request validation.</summary>
    InvalidRequest,
}
