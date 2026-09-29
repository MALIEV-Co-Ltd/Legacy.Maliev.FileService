using System.Net;
using System.Net.Http.Headers;
using Google;
using Google.Cloud.Storage.V1;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Services;

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
        long sourceGeneration;
        try
        {
            var source = await client.GetObjectAsync(sourceBucket, sourceObjectName, cancellationToken: cancellationToken);
            sourceGeneration = source.Generation is long sourceVersion && sourceVersion > 0
                ? sourceVersion
                : throw new InvalidDataException("Cloud storage did not identify the source object generation.");
        }
        catch (GoogleApiException exception) when (exception.HttpStatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        Google.Apis.Storage.v1.Data.Object copied;
        try
        {
            copied = await client.CopyObjectAsync(
                sourceBucket,
                sourceObjectName,
                destinationBucket,
                destinationObjectName,
                new CopyObjectOptions
                {
                    SourceGeneration = sourceGeneration,
                    IfSourceGenerationMatch = sourceGeneration,
                    IfGenerationMatch = 0,
                },
                cancellationToken: cancellationToken);
        }
        catch (GoogleApiException exception) when (exception.HttpStatusCode == HttpStatusCode.NotFound)
        {
            // The observed source generation disappeared after the read; a newer live source may exist.
            throw new UploadOutcomeUnknownException("Storage source generation requires reconciliation.", exception);
        }
        catch (GoogleApiException exception) when (exception.HttpStatusCode == HttpStatusCode.PreconditionFailed)
        {
            // Either the source changed or the destination already exists; never clean up by name.
            throw new UploadOutcomeUnknownException("Storage move precondition requires reconciliation.", exception);
        }
        catch (GoogleApiException exception) when (IsDefiniteClientRejection(exception.HttpStatusCode))
        {
            throw;
        }
        catch (Exception exception)
        {
            // The copy may have committed before its response was lost. Preserve both coordinates.
            throw new UploadOutcomeUnknownException("Storage copy outcome requires reconciliation.", exception);
        }

        if (copied.Generation is not long generation || generation <= 0)
        {
            throw new UploadOutcomeUnknownException(
                "Storage copy outcome requires reconciliation.",
                new InvalidDataException("Cloud storage did not identify the copied object generation."));
        }

        try
        {
            await client.DeleteObjectAsync(
                sourceBucket,
                sourceObjectName,
                new DeleteObjectOptions { IfGenerationMatch = sourceGeneration },
                cancellationToken);
            return true;
        }
        catch (GoogleApiException exception) when (exception.HttpStatusCode == HttpStatusCode.NotFound)
        {
            // The copy is complete and the quarantine object is already absent.
            return true;
        }
        catch (GoogleApiException sourceDeleteFailure) when (sourceDeleteFailure.HttpStatusCode == HttpStatusCode.PreconditionFailed)
        {
            var rollbackFailure = await TryRollbackCopyAsync(destinationBucket, destinationObjectName, generation);
            Exception cause = rollbackFailure is null
                ? sourceDeleteFailure
                : new UploadRollbackException(sourceDeleteFailure, [rollbackFailure]);
            // The live source is no longer the scanned generation. The upload layer must not delete it.
            throw new UploadOutcomeUnknownException("Storage source generation requires reconciliation.", cause);
        }
        catch (GoogleApiException sourceDeleteFailure) when (IsDefiniteClientRejection(sourceDeleteFailure.HttpStatusCode))
        {
            var rollbackFailure = await TryRollbackCopyAsync(destinationBucket, destinationObjectName, generation);
            if (rollbackFailure is not null)
            {
                throw new UploadRollbackException(sourceDeleteFailure, [rollbackFailure]);
            }

            throw;
        }
        catch (Exception exception)
        {
            // A timeout or cancellation can follow a committed delete. Never remove the only remaining copy.
            throw new UploadOutcomeUnknownException("Storage source deletion outcome requires reconciliation.", exception);
        }
    }

    private async Task<UploadCleanupFailure?> TryRollbackCopyAsync(
        string bucket,
        string objectName,
        long generation)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await client.DeleteObjectAsync(
                bucket,
                objectName,
                new DeleteObjectOptions { Generation = generation, IfGenerationMatch = generation },
                cleanup.Token);
            return null;
        }
        catch (GoogleApiException exception) when (exception.HttpStatusCode == HttpStatusCode.NotFound)
        {
            // The exact copied generation was already removed.
            return null;
        }
        catch (Exception exception)
        {
            return new UploadCleanupFailure(bucket, objectName, exception);
        }
    }

    private static bool IsDefiniteClientRejection(HttpStatusCode statusCode) =>
        (int)statusCode is >= 400 and < 500
        && statusCode is not HttpStatusCode.RequestTimeout and not HttpStatusCode.TooManyRequests;

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
        var options = CreateReadOptions(duration);
        var url = await signer.SignAsync(request, options, cancellationToken);
        return new Uri(url, UriKind.Absolute);
    }

    internal static UrlSigner.Options CreateReadOptions(TimeSpan duration) =>
        UrlSigner.Options.FromDuration(duration > TimeSpan.FromDays(7) ? TimeSpan.FromDays(7) : duration)
            .WithSigningVersion(SigningVersion.V4);

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
