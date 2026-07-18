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

    [Theory]
    [InlineData("ABCDEF")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void UploadConstructor_InvalidExpectedSha256_Throws(string checksum)
    {
        Assert.Throws<ArgumentException>(() => CreateUpload(checksum));
    }

    [Theory]
    [InlineData("ABCDEF")]
    [InlineData("BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void ActualSha256_InvalidValue_Throws(string checksum)
    {
        var upload = CreateUpload(new string('a', 64));

        Assert.Throws<ArgumentException>(() => upload.ActualSha256 = checksum);
    }

    [Fact]
    public void UploadConstructor_DurableBuckets_AreRetained()
    {
        var upload = new InstantQuoteUploadFile(
            Guid.NewGuid(), Guid.NewGuid(), Hash("key"), new string('c', 64), "part.stl", ".stl", "model/stl",
            new string('a', 64), null, null, null, "temporary-bucket", "instant-quote/opaque",
            "final-bucket", "instant-quotation/final.stl", InstantQuoteWorkflowState.Pending, Now, Now);

        Assert.Equal("temporary-bucket", upload.TemporaryBucket);
        Assert.Equal("final-bucket", upload.FinalBucket);
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

    private static InstantQuoteUploadFile CreateUpload(string expectedSha256) => new(
        Guid.NewGuid(), Guid.NewGuid(), Hash("key"), new string('c', 64), "part.stl", ".stl", "model/stl",
        expectedSha256, null, null, null, "temporary-bucket", "instant-quote/opaque", null, null,
        InstantQuoteWorkflowState.Pending, Now, Now);
}
