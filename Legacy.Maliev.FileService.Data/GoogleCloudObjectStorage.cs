using System.Net;
using System.Net.Http.Headers;
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
        var request = CreateReadRequestTemplate(bucket, objectName);
        var options = UrlSigner.Options.FromDuration(duration).WithSigningVersion(SigningVersion.V4);
        var url = await signer.SignAsync(request, options, cancellationToken);
        return new Uri(url, UriKind.Absolute);
    }

    internal static UrlSigner.RequestTemplate CreateReadRequestTemplate(string bucket, string objectName)
    {
        var fileName = objectName.Replace('\\', '/').Split('/').Last();
        if (string.IsNullOrEmpty(fileName))
        {
            throw new ArgumentException("An object filename is required.", nameof(objectName));
        }

        // The object name is storage-controlled, but must never become an unescaped response header.
        fileName = new string(fileName.Select(character =>
            character is '"' or '\\' || char.IsControl(character) ? '_' : character).ToArray());
        var asciiFileName = new string(fileName.Select(character => character <= 0x7e ? character : '_').ToArray());
        var disposition = new ContentDispositionHeaderValue("attachment")
        {
            FileName = asciiFileName,
            FileNameStar = fileName,
        };

        var parameters = new Dictionary<string, IEnumerable<string>>
        {
            ["response-content-disposition"] = [disposition.ToString()],
        };

        return UrlSigner.RequestTemplate.FromBucket(bucket)
            .WithObjectName(objectName)
            .WithHttpMethod(HttpMethod.Get)
            .WithQueryParameters(parameters);
    }
}
