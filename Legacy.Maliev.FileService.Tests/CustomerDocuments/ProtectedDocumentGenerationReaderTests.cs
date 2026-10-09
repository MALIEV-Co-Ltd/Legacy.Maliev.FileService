using Google.Cloud.Storage.V1;
using Legacy.Maliev.FileService.Application.CustomerDocuments;
using Legacy.Maliev.FileService.Data.CustomerDocuments;
using Microsoft.Extensions.Options;
namespace Legacy.Maliev.FileService.Tests.CustomerDocuments;

public sealed class ProtectedDocumentGenerationReaderTests
{
    [Fact]
    public async Task ProviderReadPinsGenerationAndMatchAndBuffersPrivateBytes()
    {
        var client = new Client();
        var reader = new CustomerDocumentGoogleCloudGenerationReader(client, Options.Create(new CustomerDocumentOptions { Enabled = true, PrivateBucket = "synthetic-private" }));
        var bytes = await reader.ReadAsync("synthetic-private", "customer-documents/23/original", 71, 10, default);
        Assert.Equal(new byte[] { 1, 2, 3 }, bytes.ToArray());
        Assert.Equal(71, client.Options!.Generation);
        Assert.Equal(71, client.Options.IfGenerationMatch);
    }
    [Fact]
    public async Task CallerCancellationIsPreservedBeforeProviderRequest()
    {
        var client = new Client(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var reader = new CustomerDocumentGoogleCloudGenerationReader(client, Options.Create(new CustomerDocumentOptions { Enabled = true, PrivateBucket = "synthetic-private" }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync("synthetic-private", "customer-documents/23/original", 71, 10, cancellation.Token));
        Assert.Null(client.Options);
    }
    [Theory]
    [InlineData(System.Net.HttpStatusCode.NotFound)]
    [InlineData(System.Net.HttpStatusCode.PreconditionFailed)]
    public async Task MissingOrReplacedGenerationDeniesWithoutPartialBytes(System.Net.HttpStatusCode status)
    {
        var client = new Client { Failure = new Google.GoogleApiException("storage", "Synthetic provider failure") { HttpStatusCode = status } };
        var reader = new CustomerDocumentGoogleCloudGenerationReader(client, Options.Create(new CustomerDocumentOptions { Enabled = true, PrivateBucket = "synthetic-private" }));
        await Assert.ThrowsAsync<DocumentAuthorityDeniedException>(() => reader.ReadAsync("synthetic-private", "customer-documents/23/original", 71, 10, default));
    }
    [Fact]
    public async Task ProviderTimeoutWithoutCallerCancellationIsUnavailable()
    {
        var client = new Client { Failure = new OperationCanceledException("Synthetic provider timeout") };
        var reader = new CustomerDocumentGoogleCloudGenerationReader(client, Options.Create(new CustomerDocumentOptions { Enabled = true, PrivateBucket = "synthetic-private" }));
        await Assert.ThrowsAsync<DocumentAuthorityUnavailableException>(() => reader.ReadAsync("synthetic-private", "customer-documents/23/original", 71, 10, default));
    }
    [Fact]
    public async Task ProviderCannotWritePastBound()
    {
        var reader = new CustomerDocumentGoogleCloudGenerationReader(new Client(), Options.Create(new CustomerDocumentOptions { Enabled = true, PrivateBucket = "synthetic-private" }));
        await Assert.ThrowsAsync<DocumentAuthorityUnavailableException>(() => reader.ReadAsync("synthetic-private", "customer-documents/23/original", 71, 2, default));
    }
    private sealed class Client : StorageClient
    {
        public DownloadObjectOptions? Options { get; private set; }
        public Exception? Failure { get; init; }
        public override async Task<Google.Apis.Storage.v1.Data.Object> DownloadObjectAsync(string bucket, string objectName, Stream destination, DownloadObjectOptions? options = null, CancellationToken cancellationToken = default, IProgress<Google.Apis.Download.IDownloadProgress>? progress = null)
        {
            Options = options;
            await destination.WriteAsync(new byte[] { 1, 2, 3 }, cancellationToken);
            if (Failure is not null) throw Failure;
            return new Google.Apis.Storage.v1.Data.Object { Bucket = bucket, Name = objectName, Generation = options!.Generation, Size = 3 };
        }
    }
}
