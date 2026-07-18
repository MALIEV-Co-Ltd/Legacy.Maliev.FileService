namespace Legacy.Maliev.FileService.Domain;

/// <summary>Durable idempotent selection of uploaded files for a quotation request.</summary>
public sealed class InstantQuoteFinalization
{
    private InstantQuoteFinalization()
    {
    }

    /// <summary>Creates a finalization reservation.</summary>
    public InstantQuoteFinalization(Guid id, Guid sessionId, byte[] idempotencyKeyHash, string requestFingerprint,
        Guid quotationRequestId, IReadOnlyCollection<Guid> selectedFileIds, InstantQuoteWorkflowState state,
        DateTimeOffset createdAt, DateTimeOffset modifiedAt)
    {
        ArgumentNullException.ThrowIfNull(idempotencyKeyHash);
        if (idempotencyKeyHash.Length != 32) throw new ArgumentException("Idempotency hashes must be SHA-256 values.", nameof(idempotencyKeyHash));
        if (requestFingerprint.Length != 64 || requestFingerprint.Any(value => !char.IsAsciiHexDigit(value) || char.IsUpper(value)))
            throw new ArgumentException("Fingerprints must be 64 lowercase hexadecimal characters.", nameof(requestFingerprint));

        Id = id;
        SessionId = sessionId;
        IdempotencyKeyHash = idempotencyKeyHash.ToArray();
        RequestFingerprint = requestFingerprint;
        QuotationRequestId = quotationRequestId;
        SelectedFileIds = selectedFileIds.Distinct().Order().ToArray();
        State = state;
        CreatedAt = createdAt;
        ModifiedAt = modifiedAt;
    }

    /// <summary>Gets the opaque finalization identifier.</summary>
    public Guid Id { get; private set; }
    /// <summary>Gets the owning session identifier.</summary>
    public Guid SessionId { get; private set; }
    /// <summary>Gets the SHA-256 idempotency-key hash.</summary>
    public byte[] IdempotencyKeyHash { get; private set; } = [];
    /// <summary>Gets the lowercase SHA-256 request fingerprint.</summary>
    public string RequestFingerprint { get; private set; } = string.Empty;
    /// <summary>Gets the quotation request identifier.</summary>
    public Guid QuotationRequestId { get; private set; }
    /// <summary>Gets selected file identifiers in deterministic order.</summary>
    public Guid[] SelectedFileIds { get; private set; } = [];
    /// <summary>Gets or sets the workflow state.</summary>
    public InstantQuoteWorkflowState State { get; set; }
    /// <summary>Gets the creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; private set; }
    /// <summary>Gets or sets the modification timestamp.</summary>
    public DateTimeOffset ModifiedAt { get; set; }
}
