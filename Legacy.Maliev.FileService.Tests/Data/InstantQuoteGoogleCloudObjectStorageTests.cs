using System.Net;
using Google;
using Google.Apis.Storage.v1.Data;
using Google.Cloud.Storage.V1;
using Legacy.Maliev.FileService.Data;
using StorageObject = Google.Apis.Storage.v1.Data.Object;

namespace Legacy.Maliev.FileService.Tests.Data;

public sealed class InstantQuoteGoogleCloudObjectStorageTests
{
    private const string Sha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task UploadTemporaryAsync_NonSeekableStream_UsesUblaCompatibleCreateOnlyRequestWithoutReplay()
    {
        var client = new RecordingClient();
        var source = new NonSeekableReadStream([1, 2, 3]);
        client.UploadHandler = (item, stream, options, token) =>
        {
            Assert.Equal("temp-bucket", item.Bucket);
            Assert.Equal("sessions/file", item.Name);
            Assert.Equal("application/octet-stream", item.ContentType);
            Assert.Equal(Sha256, item.Metadata[InstantQuoteGoogleCloudObjectStorage.ExpectedSha256MetadataKey]);
            Assert.Equal(0, options.IfGenerationMatch);
            Assert.Null(options.PredefinedAcl);
            Assert.Same(source, stream);
            Assert.Equal(CurrentCancellationToken, token);
            return Task.FromResult(Object("temp-bucket", "sessions/file", 41, 3, Sha256));
        };
        var storage = new InstantQuoteGoogleCloudObjectStorage(client);

        var result = await storage.UploadTemporaryAsync(
            "temp-bucket", "sessions/file", source, Sha256, CurrentCancellationToken);

        Assert.Equal(1, client.UploadCalls);
        Assert.Equal(41, result.Generation);
        Assert.Equal(3, result.SizeBytes);
        Assert.Equal(Sha256, result.Sha256);
    }

    [Fact]
    public async Task UploadTemporaryAsync_PreconditionFailure_DoesNotReplayOrDelete()
    {
        var client = new RecordingClient
        {
            UploadHandler = (_, _, _, _) => throw ApiException(HttpStatusCode.PreconditionFailed),
        };
        var storage = new InstantQuoteGoogleCloudObjectStorage(client);

        var exception = await Assert.ThrowsAsync<GoogleApiException>(() => storage.UploadTemporaryAsync(
            "temp-bucket", "file", new NonSeekableReadStream([1]), Sha256, CurrentCancellationToken));

        Assert.Equal(HttpStatusCode.PreconditionFailed, exception.HttpStatusCode);
        Assert.Equal(1, client.UploadCalls);
        Assert.Empty(client.DeleteRequests);
    }

    [Fact]
    public async Task UploadTemporaryAsync_MalformedReturnedMetadata_DeletesExactGenerationAndThrows()
    {
        var client = new RecordingClient
        {
            UploadHandler = (_, _, _, _) => Task.FromResult(Object("wrong-bucket", "file", 27, 1, Sha256)),
        };
        var storage = new InstantQuoteGoogleCloudObjectStorage(client);

        await Assert.ThrowsAsync<InvalidDataException>(() => storage.UploadTemporaryAsync(
            "temp-bucket", "file", new NonSeekableReadStream([1]), Sha256, CurrentCancellationToken));

        var deletion = Assert.Single(client.DeleteRequests);
        Assert.Equal("temp-bucket", deletion.Bucket);
        Assert.Equal("file", deletion.ObjectName);
        Assert.Equal(27, deletion.Options.Generation);
        Assert.Equal(27, deletion.Options.IfGenerationMatch);
    }

    [Fact]
    public async Task UploadTemporaryAsync_ChangedShaMarker_DeletesExactGenerationAndThrows()
    {
        var differentSha = new string('a', 64);
        var client = new RecordingClient
        {
            UploadHandler = (_, _, _, _) => Task.FromResult(Object("temp-bucket", "file", 28, 1, differentSha)),
        };
        var storage = new InstantQuoteGoogleCloudObjectStorage(client);

        await Assert.ThrowsAsync<InvalidDataException>(() => storage.UploadTemporaryAsync(
            "temp-bucket", "file", new NonSeekableReadStream([1]), Sha256, CurrentCancellationToken));

        var deletion = Assert.Single(client.DeleteRequests);
        Assert.Equal(28, deletion.Options.Generation);
    }

    [Fact]
    public async Task GetMetadataAsync_NotFound_ReturnsNull()
    {
        var client = new RecordingClient
        {
            GetHandler = (_, _, _, _) => throw ApiException(HttpStatusCode.NotFound),
        };
        var storage = new InstantQuoteGoogleCloudObjectStorage(client);

        var result = await storage.GetMetadataAsync("bucket", "file", CurrentCancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetMetadataAsync_PreconditionFailure_IsNotMisreportedAsMissing()
    {
        var client = new RecordingClient
        {
            GetHandler = (_, _, _, _) => throw ApiException(HttpStatusCode.PreconditionFailed),
        };
        var storage = new InstantQuoteGoogleCloudObjectStorage(client);

        var exception = await Assert.ThrowsAsync<GoogleApiException>(() =>
            storage.GetMetadataAsync("bucket", "file", CurrentCancellationToken));

        Assert.Equal(HttpStatusCode.PreconditionFailed, exception.HttpStatusCode);
    }

    [Fact]
    public async Task GetMetadataAsync_MissingShaMarker_ThrowsInvalidData()
    {
        var item = Object("bucket", "file", 7, 9, Sha256);
        item.Metadata.Clear();
        var client = new RecordingClient
        {
            GetHandler = (_, _, _, _) => Task.FromResult(item),
        };
        var storage = new InstantQuoteGoogleCloudObjectStorage(client);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            storage.GetMetadataAsync("bucket", "file", CurrentCancellationToken));
    }

    [Fact]
    public async Task GetMetadataAsync_SizeOverflow_ThrowsInvalidData()
    {
        var item = Object("bucket", "file", 7, 9, Sha256);
        item.Size = ulong.MaxValue;
        var client = new RecordingClient
        {
            GetHandler = (_, _, _, _) => Task.FromResult(item),
        };
        var storage = new InstantQuoteGoogleCloudObjectStorage(client);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            storage.GetMetadataAsync("bucket", "file", CurrentCancellationToken));
    }

    [Fact]
    public async Task DownloadGenerationAsync_UsesExactGenerationAndCancellation()
    {
        var client = new RecordingClient();
        var destination = new MemoryStream();
        var storage = new InstantQuoteGoogleCloudObjectStorage(client);

        await storage.DownloadGenerationAsync(
            "bucket", "file", 88, destination, CurrentCancellationToken);

        var request = Assert.Single(client.DownloadRequests);
        Assert.Equal(88, request.Options.Generation);
        Assert.Equal(88, request.Options.IfGenerationMatch);
        Assert.Same(destination, request.Destination);
        Assert.Equal(CurrentCancellationToken, request.CancellationToken);
    }

    [Fact]
    public async Task PromoteGenerationAsync_UsesUblaCompatibleSourceAndCreateOnlyDestinationPreconditions()
    {
        var client = new RecordingClient
        {
            CopyHandler = (_, _, destinationBucket, destinationName, _, _) =>
                Task.FromResult(Object(destinationBucket, destinationName, 101, 4, Sha256)),
        };
        var storage = new InstantQuoteGoogleCloudObjectStorage(client);

        var result = await storage.PromoteGenerationAsync(
            "temp", "source", 99, "final", "destination", CurrentCancellationToken);

        var request = Assert.Single(client.CopyRequests);
        Assert.Equal(99, request.Options.SourceGeneration);
        Assert.Equal(99, request.Options.IfSourceGenerationMatch);
        Assert.Equal(0, request.Options.IfGenerationMatch);
        Assert.Null(request.Options.DestinationPredefinedAcl);
        Assert.Equal(101, result.Generation);
    }

    [Fact]
    public async Task PromoteGenerationAsync_PreconditionFailure_IsPropagatedWithoutRetry()
    {
        var client = new RecordingClient
        {
            CopyHandler = (_, _, _, _, _, _) => throw ApiException(HttpStatusCode.PreconditionFailed),
        };
        var storage = new InstantQuoteGoogleCloudObjectStorage(client);

        await Assert.ThrowsAsync<GoogleApiException>(() => storage.PromoteGenerationAsync(
            "temp", "source", 99, "final", "destination", CurrentCancellationToken));

        Assert.Single(client.CopyRequests);
    }

    [Fact]
    public async Task PromoteGenerationAsync_MalformedReturnedMetadata_DeletesExactDestinationGenerationAndThrows()
    {
        var client = new RecordingClient
        {
            CopyHandler = (_, _, _, _, _, _) => Task.FromResult(Object("wrong-bucket", "destination", 102, 4, Sha256)),
        };
        var storage = new InstantQuoteGoogleCloudObjectStorage(client);

        await Assert.ThrowsAsync<InvalidDataException>(() => storage.PromoteGenerationAsync(
            "temp", "source", 99, "final", "destination", CurrentCancellationToken));

        var deletion = Assert.Single(client.DeleteRequests);
        Assert.Equal("final", deletion.Bucket);
        Assert.Equal("destination", deletion.ObjectName);
        Assert.Equal(102, deletion.Options.Generation);
        Assert.Equal(102, deletion.Options.IfGenerationMatch);
    }

    [Fact]
    public async Task PromoteGenerationAsync_MalformedReturnedMetadataAndCleanupRace_PreservesValidationFailure()
    {
        var client = new RecordingClient
        {
            CopyHandler = (_, _, _, _, _, _) => Task.FromResult(Object("final", "wrong-object", 103, 4, Sha256)),
            DeleteHandler = (_, _, _, _) => throw ApiException(HttpStatusCode.PreconditionFailed),
        };
        var storage = new InstantQuoteGoogleCloudObjectStorage(client);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => storage.PromoteGenerationAsync(
            "temp", "source", 99, "final", "destination", CurrentCancellationToken));

        Assert.Contains("unexpected object identity", exception.Message);
        var deletion = Assert.Single(client.DeleteRequests);
        Assert.Equal(103, deletion.Options.IfGenerationMatch);
    }

    [Fact]
    public async Task DeleteGenerationAsync_NotFound_IsIdempotent()
    {
        var client = new RecordingClient
        {
            DeleteHandler = (_, _, _, _) => throw ApiException(HttpStatusCode.NotFound),
        };
        var storage = new InstantQuoteGoogleCloudObjectStorage(client);

        await storage.DeleteGenerationAsync("bucket", "file", 123, CurrentCancellationToken);

        var request = Assert.Single(client.DeleteRequests);
        Assert.Equal(123, request.Options.Generation);
        Assert.Equal(123, request.Options.IfGenerationMatch);
    }

    [Fact]
    public async Task DeleteGenerationAsync_PreconditionFailure_IsPropagated()
    {
        var client = new RecordingClient
        {
            DeleteHandler = (_, _, _, _) => throw ApiException(HttpStatusCode.PreconditionFailed),
        };
        var storage = new InstantQuoteGoogleCloudObjectStorage(client);

        var exception = await Assert.ThrowsAsync<GoogleApiException>(() =>
            storage.DeleteGenerationAsync("bucket", "file", 123, CurrentCancellationToken));

        Assert.Equal(HttpStatusCode.PreconditionFailed, exception.HttpStatusCode);
    }

    [Fact]
    public async Task GetMetadataAsync_CanceledOperation_PropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var client = new RecordingClient
        {
            GetHandler = (_, _, _, token) => Task.FromCanceled<StorageObject>(token),
        };
        var storage = new InstantQuoteGoogleCloudObjectStorage(client);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            storage.GetMetadataAsync("bucket", "file", cancellation.Token));
    }

    private static StorageObject Object(
        string bucket,
        string objectName,
        long generation,
        ulong size,
        string sha256) => new()
        {
            Bucket = bucket,
            Name = objectName,
            Generation = generation,
            Size = size,
            Metadata = new Dictionary<string, string>
            {
                [InstantQuoteGoogleCloudObjectStorage.ExpectedSha256MetadataKey] = sha256,
            },
        };

    private static CancellationToken CurrentCancellationToken => CancellationToken.None;

    private static GoogleApiException ApiException(HttpStatusCode statusCode) => new("storage", "failure")
    {
        HttpStatusCode = statusCode,
    };

    private sealed class RecordingClient : IInstantQuoteGoogleStorageClient
    {
        public Func<StorageObject, Stream, UploadObjectOptions, CancellationToken, Task<StorageObject>> UploadHandler { get; set; } =
            (item, _, _, _) => Task.FromResult(item);

        public Func<string, string, GetObjectOptions, CancellationToken, Task<StorageObject>> GetHandler { get; set; } =
            (bucket, name, _, _) => Task.FromResult(Object(bucket, name, 1, 1, Sha256));

        public Func<string, string, string, string, CopyObjectOptions, CancellationToken, Task<StorageObject>> CopyHandler { get; set; } =
            (_, _, bucket, name, _, _) => Task.FromResult(Object(bucket, name, 1, 1, Sha256));

        public Func<string, string, DeleteObjectOptions, CancellationToken, Task> DeleteHandler { get; set; } =
            (_, _, _, _) => Task.CompletedTask;

        public int UploadCalls { get; private set; }

        public List<DownloadRequest> DownloadRequests { get; } = [];

        public List<CopyRequest> CopyRequests { get; } = [];

        public List<DeleteRequest> DeleteRequests { get; } = [];

        public Task<StorageObject> UploadObjectAsync(
            StorageObject destination,
            Stream source,
            UploadObjectOptions options,
            CancellationToken cancellationToken)
        {
            UploadCalls++;
            return UploadHandler(destination, source, options, cancellationToken);
        }

        public Task<StorageObject> GetObjectAsync(
            string bucket,
            string objectName,
            GetObjectOptions options,
            CancellationToken cancellationToken) => GetHandler(bucket, objectName, options, cancellationToken);

        public Task DownloadObjectAsync(
            string bucket,
            string objectName,
            Stream destination,
            DownloadObjectOptions options,
            CancellationToken cancellationToken)
        {
            DownloadRequests.Add(new DownloadRequest(bucket, objectName, destination, options, cancellationToken));
            return Task.CompletedTask;
        }

        public Task<StorageObject> CopyObjectAsync(
            string sourceBucket,
            string sourceObjectName,
            string destinationBucket,
            string destinationObjectName,
            CopyObjectOptions options,
            CancellationToken cancellationToken)
        {
            CopyRequests.Add(new CopyRequest(
                sourceBucket, sourceObjectName, destinationBucket, destinationObjectName, options, cancellationToken));
            return CopyHandler(
                sourceBucket, sourceObjectName, destinationBucket, destinationObjectName, options, cancellationToken);
        }

        public Task DeleteObjectAsync(
            string bucket,
            string objectName,
            DeleteObjectOptions options,
            CancellationToken cancellationToken)
        {
            DeleteRequests.Add(new DeleteRequest(bucket, objectName, options, cancellationToken));
            return DeleteHandler(bucket, objectName, options, cancellationToken);
        }
    }

    private sealed class NonSeekableReadStream(byte[] content) : MemoryStream(content, writable: false)
    {
        public override bool CanSeek => false;

        public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
    }

    private sealed record DownloadRequest(
        string Bucket,
        string ObjectName,
        Stream Destination,
        DownloadObjectOptions Options,
        CancellationToken CancellationToken);

    private sealed record CopyRequest(
        string SourceBucket,
        string SourceObjectName,
        string DestinationBucket,
        string DestinationObjectName,
        CopyObjectOptions Options,
        CancellationToken CancellationToken);

    private sealed record DeleteRequest(
        string Bucket,
        string ObjectName,
        DeleteObjectOptions Options,
        CancellationToken CancellationToken);
}
