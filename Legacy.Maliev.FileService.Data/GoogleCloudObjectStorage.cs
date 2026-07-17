using System.Net;
using Google;
using Google.Cloud.Storage.V1;
using Legacy.Maliev.FileService.Application.Interfaces;

namespace Legacy.Maliev.FileService.Data;

/// <summary>Google Cloud Storage adapter using Application Default Credentials only.</summary>
public sealed class GoogleCloudObjectStorage(StorageClient client, UrlSigner signer) : IObjectStorage
{
    /// <inheritdoc />
    public async Task UploadAsync(
        string bucket,
        string objectName,
        string contentType,
        Stream content,
        CancellationToken cancellationToken)
    {
        await client.UploadObjectAsync(
            bucket,
            objectName,
            string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
            content,
            cancellationToken: cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> MoveAsync(
        string sourceBucket,
        string sourceObjectName,
        string destinationBucket,
        string destinationObjectName,
        CancellationToken cancellationToken)
    {
        try
        {
            await client.CopyObjectAsync(
                sourceBucket,
                sourceObjectName,
                destinationBucket,
                destinationObjectName,
                cancellationToken: cancellationToken);
            await client.DeleteObjectAsync(sourceBucket, sourceObjectName, cancellationToken: cancellationToken);
            return true;
        }
        catch (GoogleApiException exception) when (exception.HttpStatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string bucket, string objectName, CancellationToken cancellationToken)
    {
        try
        {
            await client.DeleteObjectAsync(bucket, objectName, cancellationToken: cancellationToken);
            return true;
        }
        catch (GoogleApiException exception) when (exception.HttpStatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<long?> GetSizeAsync(string bucket, string objectName, CancellationToken cancellationToken)
    {
        try
        {
            var item = await client.GetObjectAsync(bucket, objectName, cancellationToken: cancellationToken);
            return item.Size is null ? null : checked((long)item.Size.Value);
        }
        catch (GoogleApiException exception) when (exception.HttpStatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<Uri> CreateSignedReadUriAsync(
        string bucket,
        string objectName,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        var url = await signer.SignAsync(
            bucket,
            objectName,
            duration,
            HttpMethod.Get,
            SigningVersion.V4,
            cancellationToken);
        return new Uri(url, UriKind.Absolute);
    }
}
