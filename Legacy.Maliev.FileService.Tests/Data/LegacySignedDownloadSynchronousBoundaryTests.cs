using System.Net.Http.Headers;
using System.Security.Cryptography;
using Google.Cloud.Storage.V1;
using Legacy.Maliev.FileService.Data;
using Moq;

namespace Legacy.Maliev.FileService.Tests.Data;

public sealed class LegacySignedDownloadSynchronousBoundaryTests
{
    [Theory]
    [InlineData(1, 3600)]
    [InlineData(168, 604800)]
    [InlineData(169, 604800)]
    [InlineData(720, 604800)]
    public void SynchronousSigning_UsesSyncSdkAndLiteralIdentityWithBoundedV4(int hours, int expectedSeconds)
    {
        var signer = ControlledSigner();
        signer.Setup(x => x.CreateSignature(It.IsAny<byte[]>(), It.IsAny<UrlSigner.BlobSignerParameters>()))
            .Returns("AQ==");
        var adapter = new GoogleCloudObjectStorage(null!, UrlSigner.FromBlobSigner(signer.Object));
        const string name = "orders/ชิ้นงาน-cafe\u0301.step ";

        var uri = adapter.GetSignedUrl("private", name, TimeSpan.FromHours(hours));

        Assert.Equal("/private/" + name, Uri.UnescapeDataString(uri.AbsolutePath));
        var query = Query(uri);
        Assert.Equal(expectedSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), query["X-Goog-Expires"]);
        Assert.Equal("GOOG4-RSA-SHA256", query["X-Goog-Algorithm"]);
        Assert.Equal("01", query["X-Goog-Signature"]);
        Assert.True(ContentDispositionHeaderValue.TryParse(query["response-content-disposition"], out var disposition));
        Assert.Equal("ชิ้นงาน-cafe\u0301.step ", disposition.FileNameStar);
        signer.Verify(x => x.CreateSignature(It.IsAny<byte[]>(), It.IsAny<UrlSigner.BlobSignerParameters>()), Times.Once);
        signer.Verify(x => x.CreateSignatureAsync(It.IsAny<byte[]>(), It.IsAny<UrlSigner.BlobSignerParameters>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void SynchronousSigning_ProducesVerifiableSyntheticRsaSignatureWithoutTransport()
    {
        using var rsa = RSA.Create(2048);
        var payloads = new List<byte[]>();
        var signer = ControlledSigner();
        signer.Setup(x => x.CreateSignature(It.IsAny<byte[]>(), It.IsAny<UrlSigner.BlobSignerParameters>()))
            .Returns((byte[] payload, UrlSigner.BlobSignerParameters _) =>
            {
                payloads.Add(payload.ToArray());
                return Convert.ToBase64String(rsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            });
        var adapter = new GoogleCloudObjectStorage(null!, UrlSigner.FromBlobSigner(signer.Object));

        var uri = adapter.GetSignedUrl("private", "orders/part.step", TimeSpan.FromDays(30));

        var payload = Assert.Single(payloads);
        var query = Query(uri);
        Assert.Equal("604800", query["X-Goog-Expires"]);
        Assert.Equal("GOOG4-RSA-SHA256", query["X-Goog-Algorithm"]);
        Assert.True(rsa.VerifyData(payload, Convert.FromHexString(query["X-Goog-Signature"]),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        signer.Verify(x => x.CreateSignatureAsync(It.IsAny<byte[]>(), It.IsAny<UrlSigner.BlobSignerParameters>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void SynchronousSigning_PropagatesExactFailureWithoutNullOrAsyncFallback()
    {
        var primary = new InvalidOperationException("Controlled signature failure.");
        var signer = ControlledSigner();
        signer.Setup(x => x.CreateSignature(It.IsAny<byte[]>(), It.IsAny<UrlSigner.BlobSignerParameters>()))
            .Throws(primary);
        var adapter = new GoogleCloudObjectStorage(null!, UrlSigner.FromBlobSigner(signer.Object));

        var actual = Assert.Throws<InvalidOperationException>(() =>
            adapter.GetSignedUrl("private", "orders/part.step", TimeSpan.FromHours(1)));

        Assert.Same(primary, actual);
        signer.Verify(x => x.CreateSignature(It.IsAny<byte[]>(), It.IsAny<UrlSigner.BlobSignerParameters>()), Times.Once);
        signer.Verify(x => x.CreateSignatureAsync(It.IsAny<byte[]>(), It.IsAny<UrlSigner.BlobSignerParameters>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static Mock<UrlSigner.IBlobSigner> ControlledSigner()
    {
        var signer = new Mock<UrlSigner.IBlobSigner>(MockBehavior.Strict);
        signer.SetupGet(x => x.Id).Returns("controlled@example.invalid");
        signer.SetupGet(x => x.Algorithm).Returns("GOOG4-RSA-SHA256");
        return signer;
    }

    private static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?').Split('&')
        .Select(value => value.Split('=', 2)).ToDictionary(value => Uri.UnescapeDataString(value[0]),
            value => Uri.UnescapeDataString(value[1]), StringComparer.Ordinal);
}
