using System.Net;
using Google;
using Google.Apis.Storage.v1.Data;
using Google.Cloud.Storage.V1;
using Legacy.Maliev.FileService.Application.Interfaces;
using StorageObject = Google.Apis.Storage.v1.Data.Object;

namespace Legacy.Maliev.FileService.Data;

/// <summary>Google Cloud Storage boundary used by the instant-quotation workflow.</summary>
public sealed class InstantQuoteGoogleCloudObjectStorage : IInstantQuoteObjectStorage
{
    internal const string ExpectedSha256MetadataKey = "maliev-expected-sha256";

    private readonly IInstantQuoteGoogleStorageClient _client;

    /// <summary>Creates an adapter backed by a Google Cloud Storage client using its configured credentials.</summary>
    public InstantQuoteGoogleCloudObjectStorage(StorageClient client)
        : this(new InstantQuoteGoogleStorageClient(client))
    {
    }

    internal InstantQuoteGoogleCloudObjectStorage(IInstantQuoteGoogleStorageClient client)
    {
        _client = client;
    }

    /// <inheritdoc />
    public async Task<InstantQuoteObjectMetadata> UploadTemporaryAsync(
        string bucket,
        string objectName,
        Stream content,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        ValidateLocation(bucket, objectName);
        ArgumentNullException.ThrowIfNull(content);
        ValidateSha256(expectedSha256);

        var request = new StorageObject
        {
            Bucket = bucket,
            Name = objectName,
            ContentType = "application/octet-stream",
            Metadata = new Dictionary<string, string>
            {
                // This marker supports reconciliation only. The application verifies the streamed digest.
                [ExpectedSha256MetadataKey] = expectedSha256,
            },
        };
        var options = new UploadObjectOptions
        {
            IfGenerationMatch = 0,
            PredefinedAcl = PredefinedObjectAcl.Private,
        };

        var uploaded = await _client.UploadObjectAsync(request, content, options, cancellationToken);
        try
        {
            var metadata = ToMetadata(uploaded, bucket, objectName);
            if (!string.Equals(metadata.Sha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Google Cloud Storage returned an unexpected reconciliation digest marker.");
            }

            return metadata;
        }
        catch (InvalidDataException)
        {
            if (TryGetPositiveGeneration(uploaded, out var generation))
            {
                await DeleteForValidationFailureAsync(bucket, objectName, generation, cancellationToken);
            }

            throw;
        }
    }

    /// <inheritdoc />
    public async Task<InstantQuoteObjectMetadata?> GetMetadataAsync(
        string bucket,
        string objectName,
        CancellationToken cancellationToken)
    {
        ValidateLocation(bucket, objectName);
        try
        {
            var item = await _client.GetObjectAsync(bucket, objectName, new GetObjectOptions(), cancellationToken);
            return ToMetadata(item, bucket, objectName);
        }
        catch (GoogleApiException exception) when (exception.HttpStatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public Task DownloadGenerationAsync(
        string bucket,
        string objectName,
        long generation,
        Stream destination,
        CancellationToken cancellationToken)
    {
        ValidateLocation(bucket, objectName);
        ValidateGeneration(generation);
        ArgumentNullException.ThrowIfNull(destination);
        return _client.DownloadObjectAsync(
            bucket,
            objectName,
            destination,
            new DownloadObjectOptions
            {
                Generation = generation,
                IfGenerationMatch = generation,
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<InstantQuoteObjectMetadata> PromoteGenerationAsync(
        string sourceBucket,
        string sourceObjectName,
        long sourceGeneration,
        string destinationBucket,
        string destinationObjectName,
        CancellationToken cancellationToken)
    {
        ValidateLocation(sourceBucket, sourceObjectName);
        ValidateLocation(destinationBucket, destinationObjectName);
        ValidateGeneration(sourceGeneration);

        var promoted = await _client.CopyObjectAsync(
            sourceBucket,
            sourceObjectName,
            destinationBucket,
            destinationObjectName,
            new CopyObjectOptions
            {
                SourceGeneration = sourceGeneration,
                IfSourceGenerationMatch = sourceGeneration,
                IfGenerationMatch = 0,
                DestinationPredefinedAcl = PredefinedObjectAcl.Private,
            },
            cancellationToken);
        try
        {
            return ToMetadata(promoted, destinationBucket, destinationObjectName);
        }
        catch (InvalidDataException)
        {
            if (TryGetPositiveGeneration(promoted, out var destinationGeneration))
            {
                await DeleteForValidationFailureAsync(
                    destinationBucket,
                    destinationObjectName,
                    destinationGeneration,
                    cancellationToken);
            }

            throw;
        }
    }

    /// <inheritdoc />
    public async Task DeleteGenerationAsync(
        string bucket,
        string objectName,
        long generation,
        CancellationToken cancellationToken)
    {
        ValidateLocation(bucket, objectName);
        ValidateGeneration(generation);
        try
        {
            await _client.DeleteObjectAsync(
                bucket,
                objectName,
                new DeleteObjectOptions
                {
                    Generation = generation,
                    IfGenerationMatch = generation,
                },
                cancellationToken);
        }
        catch (GoogleApiException exception) when (exception.HttpStatusCode == HttpStatusCode.NotFound)
        {
            // The requested immutable generation is already absent, so deletion is idempotently complete.
        }
    }

    private async Task DeleteForValidationFailureAsync(
        string bucket,
        string objectName,
        long generation,
        CancellationToken cancellationToken)
    {
        try
        {
            await DeleteGenerationAsync(bucket, objectName, generation, cancellationToken);
        }
        catch (GoogleApiException exception) when (exception.HttpStatusCode == HttpStatusCode.PreconditionFailed)
        {
            // Never delete a different generation while reporting malformed upload metadata.
        }
    }

    private static InstantQuoteObjectMetadata ToMetadata(
        StorageObject item,
        string expectedBucket,
        string expectedObjectName)
    {
        if (!string.Equals(item.Bucket, expectedBucket, StringComparison.Ordinal) ||
            !string.Equals(item.Name, expectedObjectName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Google Cloud Storage returned an unexpected object identity.");
        }

        if (!TryGetPositiveGeneration(item, out var generation))
        {
            throw new InvalidDataException("Google Cloud Storage returned an invalid object generation.");
        }

        long size;
        try
        {
            size = item.Size is null ? throw new InvalidDataException(
                "Google Cloud Storage returned no object size.") : checked((long)item.Size.Value);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("Google Cloud Storage returned an object size outside the supported range.", exception);
        }

        if (size < 0 || item.Metadata is null ||
            !item.Metadata.TryGetValue(ExpectedSha256MetadataKey, out var sha256) ||
            !IsSha256(sha256))
        {
            throw new InvalidDataException("Google Cloud Storage returned invalid reconciliation metadata.");
        }

        return new InstantQuoteObjectMetadata(item.Bucket, item.Name, generation, size, sha256.ToLowerInvariant());
    }

    private static bool TryGetPositiveGeneration(StorageObject item, out long generation)
    {
        generation = 0;
        if (item.Generation is null)
        {
            return false;
        }

        try
        {
            generation = checked((long)item.Generation.Value);
            return generation > 0;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static void ValidateLocation(string bucket, string objectName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectName);
    }

    private static void ValidateGeneration(long generation)
    {
        if (generation <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(generation), "Generation must be positive.");
        }
    }

    private static void ValidateSha256(string value)
    {
        if (!IsSha256(value))
        {
            throw new ArgumentException("Expected SHA-256 must contain exactly 64 hexadecimal characters.", nameof(value));
        }
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);
}

internal interface IInstantQuoteGoogleStorageClient
{
    Task<StorageObject> UploadObjectAsync(
        StorageObject destination,
        Stream source,
        UploadObjectOptions options,
        CancellationToken cancellationToken);

    Task<StorageObject> GetObjectAsync(
        string bucket,
        string objectName,
        GetObjectOptions options,
        CancellationToken cancellationToken);

    Task DownloadObjectAsync(
        string bucket,
        string objectName,
        Stream destination,
        DownloadObjectOptions options,
        CancellationToken cancellationToken);

    Task<StorageObject> CopyObjectAsync(
        string sourceBucket,
        string sourceObjectName,
        string destinationBucket,
        string destinationObjectName,
        CopyObjectOptions options,
        CancellationToken cancellationToken);

    Task DeleteObjectAsync(
        string bucket,
        string objectName,
        DeleteObjectOptions options,
        CancellationToken cancellationToken);
}

internal sealed class InstantQuoteGoogleStorageClient(StorageClient client) : IInstantQuoteGoogleStorageClient
{
    public Task<StorageObject> UploadObjectAsync(
        StorageObject destination,
        Stream source,
        UploadObjectOptions options,
        CancellationToken cancellationToken) =>
        client.UploadObjectAsync(destination, source, options, cancellationToken);

    public Task<StorageObject> GetObjectAsync(
        string bucket,
        string objectName,
        GetObjectOptions options,
        CancellationToken cancellationToken) =>
        client.GetObjectAsync(bucket, objectName, options, cancellationToken);

    public Task DownloadObjectAsync(
        string bucket,
        string objectName,
        Stream destination,
        DownloadObjectOptions options,
        CancellationToken cancellationToken) =>
        client.DownloadObjectAsync(bucket, objectName, destination, options, cancellationToken);

    public Task<StorageObject> CopyObjectAsync(
        string sourceBucket,
        string sourceObjectName,
        string destinationBucket,
        string destinationObjectName,
        CopyObjectOptions options,
        CancellationToken cancellationToken) =>
        client.CopyObjectAsync(
            sourceBucket,
            sourceObjectName,
            destinationBucket,
            destinationObjectName,
            options,
            cancellationToken);

    public Task DeleteObjectAsync(
        string bucket,
        string objectName,
        DeleteObjectOptions options,
        CancellationToken cancellationToken) =>
        client.DeleteObjectAsync(bucket, objectName, options, cancellationToken);
}
