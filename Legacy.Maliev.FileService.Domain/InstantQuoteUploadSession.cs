using System.Security.Cryptography;

namespace Legacy.Maliev.FileService.Domain;

/// <summary>Durable capability and owner binding for an instant-quotation upload session.</summary>
public sealed class InstantQuoteUploadSession
{
    private InstantQuoteUploadSession()
    {
    }

    /// <summary>Creates an instant-quotation upload session.</summary>
    public InstantQuoteUploadSession(Guid id, string? ownerSubject, bool isAuthenticated, byte[] tokenHash, DateTimeOffset expiresAt, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(tokenHash);
        if (tokenHash.Length != 32)
        {
            throw new ArgumentException("Session token hashes must be SHA-256 values.", nameof(tokenHash));
        }

        Id = id;
        OwnerSubject = ownerSubject;
        IsAuthenticated = isAuthenticated;
        TokenHash = tokenHash.ToArray();
        ExpiresAt = expiresAt;
        CreatedAt = createdAt;
    }

    /// <summary>Gets the opaque session identifier.</summary>
    public Guid Id { get; private set; }

    /// <summary>Gets the issuer-qualified user subject or service-client identity.</summary>
    public string? OwnerSubject { get; private set; }

    /// <summary>Gets whether the owner was authenticated.</summary>
    public bool IsAuthenticated { get; private set; }

    /// <summary>Gets the SHA-256 session capability hash.</summary>
    public byte[] TokenHash { get; private set; } = [];

    /// <summary>Gets the UTC expiry instant.</summary>
    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>Gets the UTC creation instant.</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Verifies the capability, expiry, and exact owner binding.</summary>
    public bool VerifyOwnership(byte[] presentedTokenHash, string? ownerSubject, bool isAuthenticated, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(presentedTokenHash);
        var tokenMatches = presentedTokenHash.Length == TokenHash.Length
            && CryptographicOperations.FixedTimeEquals(TokenHash, presentedTokenHash);
        var ownerMatches = IsAuthenticated == isAuthenticated
            && string.Equals(OwnerSubject, ownerSubject, StringComparison.Ordinal);
        return tokenMatches && ownerMatches && now < ExpiresAt;
    }
}
