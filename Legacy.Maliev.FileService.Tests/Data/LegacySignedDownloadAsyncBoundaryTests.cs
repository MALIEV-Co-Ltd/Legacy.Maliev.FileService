using System.Net.Http.Headers;
using Google.Cloud.Storage.V1;
using Legacy.Maliev.FileService.Data;
using Moq;

namespace Legacy.Maliev.FileService.Tests.Data;

// Ordinary async/bounded-download contract only: controlled signer, no cloud or SDK qualification.
public sealed class LegacySignedDownloadAsyncBoundaryTests
{
    [Fact]
    public async Task AsyncSigning_WaitsForSignatureAndPreservesLiteralThaiIdentity()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var signature = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var signer = Signer((_, _, token) =>
        {
            Assert.Equal(deadline.Token, token);
            entered.TrySetResult();
            return signature.Task;
        });
        const string name = "orders/ชิ้นงาน-cafe\u0301.step ";
        var adapter = new GoogleCloudObjectStorage(null!, UrlSigner.FromBlobSigner(signer.Object));
        var pending = adapter.CreateSignedReadUriAsync("private", name, TimeSpan.FromDays(30), deadline.Token);
        Exception? primaryFailure = null;
        try
        {
            await entered.Task.WaitAsync(deadline.Token);
            Assert.False(pending.IsCompleted);
            signature.SetResult("AQ==");
            var uri = await pending.WaitAsync(deadline.Token);
            Assert.Equal("/private/" + name, Uri.UnescapeDataString(uri.AbsolutePath));
            var query = Query(uri);
            Assert.Equal("604800", query["X-Goog-Expires"]);
            Assert.Equal("GOOG4-RSA-SHA256", query["X-Goog-Algorithm"]);
            Assert.True(ContentDispositionHeaderValue.TryParse(query["response-content-disposition"], out var disposition));
            Assert.Equal("ชิ้นงาน-cafe\u0301.step ", disposition.FileNameStar);
            signer.Verify(x => x.CreateSignatureAsync(It.IsAny<byte[]>(), It.IsAny<UrlSigner.BlobSignerParameters>(), deadline.Token), Times.Once);
        }
        catch (Exception error)
        {
            primaryFailure = error;
            throw;
        }
        finally
        {
            try
            {
                signature.TrySetResult("AQ==");
                deadline.Cancel();
                await SettleAsync(pending);
            }
            catch (Exception cleanupError) when (primaryFailure is not null)
            {
                throw new AggregateException("Signing assertion and settlement both failed.", primaryFailure, cleanupError);
            }
        }
    }

    [Fact]
    public async Task AsyncSigning_CancellationReachesPendingSignatureAndSettlesCaller()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var signer = Signer(async (_, _, token) =>
        {
            Assert.Equal(deadline.Token, token);
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return "AQ==";
        });
        var adapter = new GoogleCloudObjectStorage(null!, UrlSigner.FromBlobSigner(signer.Object));
        var pending = adapter.CreateSignedReadUriAsync("private", "orders/ชิ้นงาน.step", TimeSpan.FromHours(1), deadline.Token);
        Exception? primaryFailure = null;
        try
        {
            await entered.Task.WaitAsync(deadline.Token);
            Assert.False(pending.IsCompleted);
            deadline.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(pending.IsCompleted);
        }
        catch (Exception error)
        {
            primaryFailure = error;
            throw;
        }
        finally
        {
            try
            {
                deadline.Cancel();
                await SettleAsync(pending);
            }
            catch (Exception cleanupError) when (primaryFailure is not null)
            {
                throw new AggregateException("Signing assertion and settlement both failed.", primaryFailure, cleanupError);
            }
        }
    }

    [Fact]
    public async Task AsyncGenerationSigning_BindsGenerationAndSevenDayExpiryWithoutChangingObjectIdentity()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var signer = Signer((_, _, token) =>
        {
            Assert.Equal(deadline.Token, token);
            return Task.FromResult("AQ==");
        });
        var adapter = new GoogleCloudObjectStorage(null!, UrlSigner.FromBlobSigner(signer.Object));
        const string name = "orders/ชิ้นงาน-cafe\u0301.step";
        var uri = await adapter.CreateSignedGenerationReadUriAsync("private", name, 31,
            TimeSpan.FromDays(30), deadline.Token);
        var query = Query(uri);
        Assert.Equal("/private/" + name, Uri.UnescapeDataString(uri.AbsolutePath));
        Assert.Equal("31", query["generation"]);
        Assert.Equal("604800", query["X-Goog-Expires"]);
        Assert.Equal("GOOG4-RSA-SHA256", query["X-Goog-Algorithm"]);
        signer.Verify(x => x.CreateSignatureAsync(It.IsAny<byte[]>(), It.IsAny<UrlSigner.BlobSignerParameters>(), deadline.Token), Times.Once);
    }

    private static Mock<UrlSigner.IBlobSigner> Signer(Func<byte[], UrlSigner.BlobSignerParameters, CancellationToken, Task<string>> sign)
    {
        var signer = new Mock<UrlSigner.IBlobSigner>(MockBehavior.Strict);
        signer.SetupGet(x => x.Id).Returns("controlled@example.invalid");
        signer.SetupGet(x => x.Algorithm).Returns("GOOG4-RSA-SHA256");
        signer.Setup(x => x.CreateSignatureAsync(It.IsAny<byte[]>(), It.IsAny<UrlSigner.BlobSignerParameters>(), It.IsAny<CancellationToken>()))
            .Returns((byte[] payload, UrlSigner.BlobSignerParameters parameters, CancellationToken token) => sign(payload, parameters, token));
        return signer;
    }

    private static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?').Split('&')
        .Select(value => value.Split('=', 2)).ToDictionary(value => Uri.UnescapeDataString(value[0]),
            value => Uri.UnescapeDataString(value[1]), StringComparer.Ordinal);

    private static async Task SettleAsync(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { }
    }
}
