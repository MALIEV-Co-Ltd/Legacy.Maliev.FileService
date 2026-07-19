using Google.Cloud.Storage.V1;
using Legacy.Maliev.FileService.Application.Interfaces;

namespace Legacy.Maliev.FileService.Data;

/// <summary>Checks GCS bucket metadata access using Application Default Credentials.</summary>
public sealed class InstantQuoteGoogleCloudReadinessProbe : IInstantQuoteObjectStorageReadinessProbe
{
    private readonly IInstantQuoteGoogleBucketReadinessClient _client;

    /// <summary>Creates a bucket metadata probe backed by the configured GCS client.</summary>
    public InstantQuoteGoogleCloudReadinessProbe(StorageClient client)
        : this(new InstantQuoteGoogleBucketReadinessClient(client))
    {
    }

    internal InstantQuoteGoogleCloudReadinessProbe(IInstantQuoteGoogleBucketReadinessClient client)
    {
        _client = client;
    }

    /// <inheritdoc />
    public Task CheckBucketAsync(string bucket, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        return _client.GetBucketMetadataAsync(bucket, cancellationToken);
    }
}

internal interface IInstantQuoteGoogleBucketReadinessClient
{
    Task GetBucketMetadataAsync(string bucket, CancellationToken cancellationToken);
}

internal sealed class InstantQuoteGoogleBucketReadinessClient(StorageClient client)
    : IInstantQuoteGoogleBucketReadinessClient
{
    public async Task GetBucketMetadataAsync(string bucket, CancellationToken cancellationToken)
    {
        _ = await client.GetBucketAsync(
            bucket,
            new GetBucketOptions
            {
                Projection = Projection.NoAcl,
            },
            cancellationToken);
    }
}
