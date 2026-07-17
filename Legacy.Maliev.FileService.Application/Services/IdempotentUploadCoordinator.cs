using System.Security.Cryptography;
using System.Text;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Models;

namespace Legacy.Maliev.FileService.Application.Services;

/// <summary>Coordinates replay-safe ownership around the existing scanned-upload workflow.</summary>
/// <param name="store">The durable fenced checkpoint store.</param>
public sealed class IdempotentUploadCoordinator(IUploadIdempotencyStore store)
{
    /// <summary>Gets or initializes the interval used to renew active upload ownership.</summary>
    public TimeSpan RenewalInterval { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Gets or initializes the clock used to persist a stable legacy default path.</summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>Executes, replays, or reconciles a caller-bound scanned upload.</summary>
    /// <returns>The exact completed or reconciled upload response.</returns>
    public async Task<UploadResultResponse> ExecuteAsync(
        string principalId, string? workflowKey, string bucket, string? path, IReadOnlyList<IUploadFile> files,
        Func<Guid, string?, CancellationToken, Task<UploadResultResponse>> execute,
        Func<Guid, string?, CancellationToken, Task<UploadResultResponse?>> reconcile,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workflowKey)) return await execute(Guid.NewGuid(), path, cancellationToken);
        var identity = Identity(principalId, workflowKey);
        var fingerprint = await FingerprintAsync(bucket, path, files, cancellationToken);
        var generation = Generation(identity, fingerprint);
        var date = Clock.GetUtcNow();
        var effectivePath = string.IsNullOrWhiteSpace(path) ? $"uploads/{date.Year}-{date.Month}-{date.Day}/{generation}" : path;
        UploadAcquireResult acquired;
        try { acquired = await store.AcquireAsync(identity, fingerprint, effectivePath, cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException) { throw new UploadIdempotencyUnavailableException("Upload replay protection is unavailable.", exception); }

        if (acquired.State == UploadAcquireState.Replay) return acquired.Response ?? throw new UploadIdempotencyUnavailableException("Stored upload response is invalid.");
        if (acquired.State == UploadAcquireState.Conflict) throw new UploadIdempotencyConflictException("Idempotency-Key was already used for a different upload.");
        if (acquired.State == UploadAcquireState.InProgress) throw new UploadIdempotencyInProgressException("This upload is already in progress.");
        if (acquired.State == UploadAcquireState.Unknown)
        {
            if (acquired.Response is not null)
            {
                try { await store.CompleteAsync(identity, fingerprint, acquired.ReservationId!, acquired.Response, cancellationToken); }
                catch (Exception exception) { throw new UploadOutcomeUnknownException("Stored upload response could not be finalized.", exception); }
                return acquired.Response;
            }
            UploadResultResponse? reconciled;
            try { reconciled = await reconcile(generation, acquired.EffectivePath, cancellationToken); }
            catch (Exception exception) { throw new UploadOutcomeUnknownException("Upload reconciliation is temporarily unavailable.", exception); }
            if (reconciled is null) throw new UploadOutcomeUnknownException("The prior upload outcome requires reconciliation.");
            try { await store.CompleteAsync(identity, fingerprint, acquired.ReservationId!, reconciled, cancellationToken); }
            catch (Exception exception) { throw new UploadOutcomeUnknownException("Reconciled upload could not be finalized.", exception); }
            return reconciled;
        }

        var reservation = acquired.ReservationId ?? throw new UploadIdempotencyUnavailableException("Upload reservation is missing.");
        try
        {
            if (!await store.RenewAsync(identity, reservation, cancellationToken))
            {
                await MarkUnknownAsync(identity, reservation, null);
                throw new UploadOutcomeUnknownException("Upload ownership was lost before execution.");
            }
        }
        catch (UploadOutcomeUnknownException) { throw; }
        catch (Exception exception) { await ReleaseAsync(identity, reservation); throw new UploadIdempotencyUnavailableException("Upload ownership could not be confirmed.", exception); }
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ownershipLost = false;
        var renew = RenewAsync();
        UploadResultResponse response;
        try { response = await execute(generation, acquired.EffectivePath, execution.Token); }
        catch (Exception exception) when (exception is FileUploadValidationException or MalwareDetectedException or MalwareScannerUnavailableException)
        { execution.Cancel(); await renew; await ReleaseAsync(identity, reservation); throw; }
        catch (Exception exception)
        { execution.Cancel(); await renew; await MarkUnknownAsync(identity, reservation, null); throw new UploadOutcomeUnknownException("Upload outcome requires reconciliation.", exception); }
        execution.Cancel(); await renew;
        if (ownershipLost) { await MarkUnknownAsync(identity, reservation, response); throw new UploadOutcomeUnknownException("Upload ownership was lost during execution."); }
        try { await store.CompleteAsync(identity, fingerprint, reservation, response, cancellationToken); }
        catch (Exception exception) { await MarkUnknownAsync(identity, reservation, response); throw new UploadOutcomeUnknownException("Upload completed but replay checkpointing is uncertain.", exception); }
        return response;

        async Task RenewAsync()
        {
            try
            {
                while (true)
                {
                    await Task.Delay(RenewalInterval, execution.Token);
                    if (!await store.RenewAsync(identity, reservation, execution.Token)) { ownershipLost = true; execution.Cancel(); return; }
                }
            }
            catch (OperationCanceledException) when (execution.IsCancellationRequested) { }
            catch { ownershipLost = true; execution.Cancel(); }
        }
    }

    private async Task ReleaseAsync(string identity, string reservation) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)); try { await store.ReleaseAsync(identity, reservation, timeout.Token); } catch { } }
    private async Task MarkUnknownAsync(string identity, string reservation, UploadResultResponse? response) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)); try { await store.MarkUnknownAsync(identity, reservation, response, timeout.Token); } catch { } }
    private static string Identity(string principal, string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{principal.Trim()}\n{key.Trim()}")));
    private static Guid Generation(string identity, string fingerprint) => new(SHA256.HashData(Encoding.UTF8.GetBytes($"{identity}\n{fingerprint}"))[..16]);
    private static async Task<string> FingerprintAsync(string bucket, string? path, IReadOnlyList<IUploadFile> files, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); Add(hash, bucket.Trim().ToLowerInvariant()); Add(hash, (path ?? string.Empty).Replace('\\', '/').Trim('/').ToLowerInvariant());
        foreach (var file in files) { Add(hash, Path.GetFileName(file.FileName).Trim().ToLowerInvariant()); Add(hash, file.ContentType.Trim().ToLowerInvariant()); Add(hash, file.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)); await using var content = file.OpenReadStream(); var buffer = new byte[81920]; int read; while ((read = await content.ReadAsync(buffer, cancellationToken)) != 0) hash.AppendData(buffer, 0, read); }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static void Add(IncrementalHash hash, string value) { hash.AppendData(Encoding.UTF8.GetBytes(value)); hash.AppendData([0]); }
}
