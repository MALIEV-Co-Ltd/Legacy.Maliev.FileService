using System.Security.Cryptography;
using System.Text;
using Legacy.Maliev.FileService.Domain;

namespace Legacy.Maliev.FileService.Tests.Application;

public sealed class InstantQuoteOwnershipTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-18T12:00:00Z");

    [Fact]
    public void VerifyOwnership_MatchingTokenAndAuthenticatedOwner_ReturnsTrue()
    {
        var tokenHash = Hash("session-token");
        var session = CreateSession(tokenHash, "https://issuer.example|user-42", true);

        var verified = session.VerifyOwnership(
            tokenHash,
            "https://issuer.example|user-42",
            isAuthenticated: true,
            Now);

        Assert.True(verified);
    }

    [Theory]
    [InlineData("wrong-token", "https://issuer.example|user-42", true, 0)]
    [InlineData("session-token", "https://issuer.example|other-user", true, 0)]
    [InlineData("session-token", "https://issuer.example|user-42", false, 0)]
    [InlineData("session-token", "https://issuer.example|user-42", true, 2)]
    public void VerifyOwnership_InvalidCapabilityOrOwner_ReturnsFalse(
        string token,
        string ownerSubject,
        bool isAuthenticated,
        int hoursAfterExpiry)
    {
        var session = CreateSession(Hash("session-token"), "https://issuer.example|user-42", true);

        var verified = session.VerifyOwnership(
            Hash(token),
            ownerSubject,
            isAuthenticated,
            Now.AddHours(hoursAfterExpiry));

        Assert.False(verified);
    }

    [Fact]
    public void VerifyOwnership_AnonymousSessionRequiresCapabilityButNoSubject()
    {
        var tokenHash = Hash("anonymous-token");
        var session = CreateSession(tokenHash, ownerSubject: null, isAuthenticated: false);

        Assert.True(session.VerifyOwnership(tokenHash, null, isAuthenticated: false, Now));
        Assert.False(session.VerifyOwnership(tokenHash, "service-client", isAuthenticated: true, Now));
    }

    [Fact]
    public void Constructor_PlaintextSizedOrMalformedHashes_Throws()
    {
        Assert.Throws<ArgumentException>(() => CreateSession(Encoding.UTF8.GetBytes("plaintext"), null, false));
    }

    private static InstantQuoteUploadSession CreateSession(
        byte[] tokenHash,
        string? ownerSubject,
        bool isAuthenticated)
    {
        return new InstantQuoteUploadSession(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            ownerSubject,
            isAuthenticated,
            tokenHash,
            Now.AddHours(1),
            Now);
    }

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}
