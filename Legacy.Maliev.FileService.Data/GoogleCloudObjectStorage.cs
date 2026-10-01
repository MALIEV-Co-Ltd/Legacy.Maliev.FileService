using System.Net;
using System.Net.Http.Headers;
using Google;
using Google.Cloud.Storage.V1;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Services;
using StorageObject = Google.Apis.Storage.v1.Data.Object;

namespace Legacy.Maliev.FileService.Data;

/// <summary>Google Cloud Storage adapter using Application Default Credentials only.</summary>
public sealed class GoogleCloudObjectStorage(StorageClient client, UrlSigner signer, IStorageMoveJournal? journal = null) : IObjectStorage
{
    /// <inheritdoc />
    public async Task UploadAsync(
        string bucket,
        string objectName,
        string contentType,
        Stream content,
        CancellationToken cancellationToken)
    {
        _ = await UploadGenerationAsync(bucket, objectName, contentType, content, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<long> UploadGenerationAsync(string bucket, string objectName, string contentType,
        Stream content, CancellationToken cancellationToken)
    {
        var uploaded = await client.UploadObjectAsync(
            new StorageObject
            {
                Bucket = bucket,
                Name = objectName,
                ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
            },
            content,
            new UploadObjectOptions { IfGenerationMatch = 0 },
            cancellationToken);
        return uploaded.Generation is long generation && generation > 0
            ? generation
            : throw new UploadOutcomeUnknownException("Quarantine generation requires reconciliation.");
    }

    /// <inheritdoc />
    public Task<bool> MoveAsync(
        string sourceBucket,
        string sourceObjectName,
        string destinationBucket,
        string destinationObjectName,
        CancellationToken cancellationToken) =>
        MoveCoreAsync(null, null, false, sourceBucket, sourceObjectName, destinationBucket, destinationObjectName, cancellationToken);

    /// <inheritdoc />
    public Task<bool> MoveJournaledAsync(Guid operationId, long? expectedSourceGeneration, bool scanClean, string sourceBucket, string sourceObjectName,
        string destinationBucket, string destinationObjectName, CancellationToken cancellationToken) =>
        MoveCoreAsync(operationId, expectedSourceGeneration, scanClean, sourceBucket, sourceObjectName, destinationBucket, destinationObjectName, cancellationToken);

    private async Task<bool> MoveCoreAsync(Guid? operationId, long? expectedSourceGeneration, bool scanClean, string sourceBucket, string sourceObjectName,
        string destinationBucket, string destinationObjectName, CancellationToken cancellationToken)
    {
        var journalStarted = false;
        if (operationId is Guid knownId && expectedSourceGeneration is long knownGeneration)
        {
            if (journal is null) throw new InvalidOperationException("Storage move journal is unavailable.");
            if (!await journal.BeginAsync(knownId, scanClean, sourceBucket, sourceObjectName, knownGeneration,
                destinationBucket, destinationObjectName, cancellationToken))
            {
                throw new UploadOutcomeUnknownException("Storage move already has a durable checkpoint.");
            }
            journalStarted = true;
        }

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
            if (journalStarted)
            {
                await MarkUnknownAsync(operationId);
                throw new UploadOutcomeUnknownException("Scanned quarantine generation requires reconciliation.", exception);
            }
            return false;
        }
        catch (Exception exception) when (journalStarted)
        {
            await MarkUnknownAsync(operationId);
            throw new UploadOutcomeUnknownException("Scanned quarantine read requires reconciliation.", exception);
        }

        if (expectedSourceGeneration is long expected && sourceGeneration != expected)
        {
            await MarkUnknownAsync(operationId);
            throw new UploadOutcomeUnknownException("Quarantine generation changed after scanning.");
        }

        if (operationId is Guid id && !journalStarted)
        {
            if (journal is null) throw new InvalidOperationException("Storage move journal is unavailable.");
            if (!await journal.BeginAsync(id, scanClean, sourceBucket, sourceObjectName, sourceGeneration,
                destinationBucket, destinationObjectName, cancellationToken))
            {
                throw new UploadOutcomeUnknownException("Storage move already has a durable checkpoint.");
            }
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
            await MarkUnknownAsync(operationId);
            // The observed source generation disappeared after the read; a newer live source may exist.
            throw new UploadOutcomeUnknownException("Storage source generation requires reconciliation.", exception);
        }
        catch (GoogleApiException exception) when (exception.HttpStatusCode == HttpStatusCode.PreconditionFailed)
        {
            await MarkUnknownAsync(operationId);
            // Either the source changed or the destination already exists; never clean up by name.
            throw new UploadOutcomeUnknownException("Storage move precondition requires reconciliation.", exception);
        }
        catch (GoogleApiException exception) when (IsDefiniteClientRejection(exception.HttpStatusCode))
        {
            await MarkUnknownAsync(operationId);
            if (operationId is not null)
                throw new UploadOutcomeUnknownException("Storage copy was rejected after checkpointing.", exception);
            throw;
        }
        catch (Exception exception)
        {
            await MarkUnknownAsync(operationId);
            // The copy may have committed before its response was lost. Preserve both coordinates.
            throw new UploadOutcomeUnknownException("Storage copy outcome requires reconciliation.", exception);
        }

        if (copied.Generation is not long generation || generation <= 0)
        {
            await MarkUnknownAsync(operationId);
            throw new UploadOutcomeUnknownException(
                "Storage copy outcome requires reconciliation.",
                new InvalidDataException("Cloud storage did not identify the copied object generation."));
        }

        if (operationId is Guid copiedId)
        {
            try { await journal!.CopiedAsync(copiedId, generation, cancellationToken); }
            catch (Exception exception)
            {
                await MarkUnknownAsync(operationId);
                throw new UploadOutcomeUnknownException("Copied generation checkpoint requires reconciliation.", exception);
            }
        }

        try
        {
            await client.DeleteObjectAsync(
                sourceBucket,
                sourceObjectName,
                new DeleteObjectOptions { IfGenerationMatch = sourceGeneration },
                cancellationToken);
            if (operationId is Guid deletedId) await journal!.SourceDeletedAsync(deletedId, cancellationToken);
            return true;
        }
        catch (GoogleApiException exception) when (exception.HttpStatusCode == HttpStatusCode.NotFound)
        {
            try
            {
                if (operationId is Guid deletedId) await journal!.SourceDeletedAsync(deletedId, cancellationToken);
            }
            catch (Exception checkpointFailure)
            {
                await MarkUnknownAsync(operationId);
                throw new UploadOutcomeUnknownException("Storage source deletion checkpoint requires reconciliation.", checkpointFailure);
            }
            // The copy is complete and the quarantine object is already absent.
            return true;
        }
        catch (GoogleApiException sourceDeleteFailure) when (sourceDeleteFailure.HttpStatusCode == HttpStatusCode.PreconditionFailed)
        {
            var rollbackFailure = await TryRollbackCopyAsync(destinationBucket, destinationObjectName, generation);
            await MarkUnknownAsync(operationId);
            Exception cause = rollbackFailure is null
                ? sourceDeleteFailure
                : new UploadRollbackException(sourceDeleteFailure, [rollbackFailure]);
            // The live source is no longer the scanned generation. The upload layer must not delete it.
            throw new UploadOutcomeUnknownException("Storage source generation requires reconciliation.", cause);
        }
        catch (GoogleApiException sourceDeleteFailure) when (IsDefiniteClientRejection(sourceDeleteFailure.HttpStatusCode))
        {
            var rollbackFailure = await TryRollbackCopyAsync(destinationBucket, destinationObjectName, generation);
            await MarkUnknownAsync(operationId);
            if (rollbackFailure is not null)
            {
                if (operationId is not null)
                    throw new UploadOutcomeUnknownException("Storage rollback requires reconciliation.",
                        new UploadRollbackException(sourceDeleteFailure, [rollbackFailure]));
                throw new UploadRollbackException(sourceDeleteFailure, [rollbackFailure]);
            }

            if (operationId is not null)
                throw new UploadOutcomeUnknownException("Storage source deletion was rejected after checkpointing.", sourceDeleteFailure);
            throw;
        }
        catch (Exception exception)
        {
            await MarkUnknownAsync(operationId);
            // A timeout or cancellation can follow a committed delete. Never remove the only remaining copy.
            throw new UploadOutcomeUnknownException("Storage source deletion outcome requires reconciliation.", exception);
        }
    }

    private async Task MarkUnknownAsync(Guid? operationId)
    {
        if (operationId is not Guid id || journal is null) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await journal.UnknownAsync(id, timeout.Token); }
        catch { /* The durable source-observed row still permits operator reconciliation. */ }
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
    public async Task<bool> DeleteGenerationAsync(string bucket, string objectName, long generation, CancellationToken cancellationToken)
    {
        if (generation <= 0) throw new ArgumentOutOfRangeException(nameof(generation));
        try
        {
            await client.DeleteObjectAsync(bucket, objectName,
                new DeleteObjectOptions { Generation = generation, IfGenerationMatch = generation }, cancellationToken);
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
    public async Task<StorageObjectEvidence?> GetEvidenceAsync(string bucket, string objectName, CancellationToken cancellationToken)
    {
        try
        {
            var item = await client.GetObjectAsync(bucket, objectName, cancellationToken: cancellationToken);
            return item.Generation is long generation && generation > 0 && item.Size is ulong size && size <= long.MaxValue
                ? new StorageObjectEvidence(generation, (long)size)
                : throw new InvalidDataException("Cloud storage object generation or size is unavailable.");
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
