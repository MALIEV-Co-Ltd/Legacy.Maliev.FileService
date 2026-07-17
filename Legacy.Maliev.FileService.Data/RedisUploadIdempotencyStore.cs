using System.Text.Json;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Models;
using StackExchange.Redis;

namespace Legacy.Maliev.FileService.Data;

/// <summary>Redis-backed fenced upload checkpoint retained for replay and reconciliation.</summary>
/// <param name="redis">The existing shared Redis connection.</param>
public sealed class RedisUploadIdempotencyStore(IConnectionMultiplexer? redis = null) : IUploadIdempotencyStore
{
    private static readonly TimeSpan Retention = TimeSpan.FromHours(24);
    private static readonly TimeSpan LeaseLifetime = TimeSpan.FromMinutes(1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public async Task<UploadAcquireResult> AcquireAsync(string identity, string fingerprint, string effectivePath, CancellationToken cancellationToken)
    {
        var database = Database();
        var key = Key(identity);
        var leaseKey = LeaseKey(identity); var reservation = Guid.NewGuid().ToString("N");
        var pending = Serialize(new Envelope(fingerprint, reservation, "pending", null, effectivePath));
        var create = database.CreateTransaction(); create.AddCondition(Condition.KeyNotExists(key)); create.AddCondition(Condition.KeyNotExists(leaseKey));
        _ = create.StringSetAsync(key, pending, Retention); _ = create.StringSetAsync(leaseKey, reservation, LeaseLifetime);
        if (await create.ExecuteAsync().WaitAsync(cancellationToken))
            return new(UploadAcquireState.Acquired, reservation, EffectivePath: effectivePath);
        var current = await database.StringGetAsync(key).WaitAsync(cancellationToken);
        if (!current.HasValue) throw new InvalidOperationException("Upload checkpoint changed during acquisition.");
        var envelope = Deserialize(current!);
        if (!string.Equals(envelope.Fingerprint, fingerprint, StringComparison.Ordinal)) return new(UploadAcquireState.Conflict);
        if (envelope.State == "pending")
        {
            var lease = await database.StringGetAsync(leaseKey).WaitAsync(cancellationToken);
            if (lease == envelope.ReservationId) return new(UploadAcquireState.InProgress, EffectivePath: envelope.EffectivePath);
            var unknown = Serialize(envelope with { State = "unknown" });
            _ = await ReplaceAsync(database, key, current!, unknown, cancellationToken);
            return new(UploadAcquireState.Unknown, envelope.ReservationId, envelope.Response, envelope.EffectivePath);
        }
        return envelope.State switch
        {
            "completed" when envelope.Response is not null => new(UploadAcquireState.Replay, Response: envelope.Response, EffectivePath: envelope.EffectivePath),
            "unknown" => new(UploadAcquireState.Unknown, envelope.ReservationId, envelope.Response, envelope.EffectivePath),
            _ => new(UploadAcquireState.Unknown, envelope.ReservationId, envelope.Response, envelope.EffectivePath),
        };
    }

    /// <inheritdoc />
    public async Task<bool> RenewAsync(string identity, string reservationId, CancellationToken cancellationToken)
    {
        var database = Database(); var key = Key(identity); var leaseKey = LeaseKey(identity); var current = await database.StringGetAsync(key).WaitAsync(cancellationToken);
        if (!current.HasValue || !Owned(current!, reservationId, "pending")) return false;
        var envelope = Deserialize(current!);
        var transaction = database.CreateTransaction(); transaction.AddCondition(Condition.StringEqual(key, current));
        if (envelope.State == "pending") transaction.AddCondition(Condition.StringEqual(leaseKey, reservationId));
        _ = transaction.StringSetAsync(leaseKey, reservationId, LeaseLifetime);
        return await transaction.ExecuteAsync().WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task CompleteAsync(string identity, string fingerprint, string reservationId, UploadResultResponse response, CancellationToken cancellationToken)
    {
        var database = Database(); var key = Key(identity); var leaseKey = LeaseKey(identity); var current = await database.StringGetAsync(key).WaitAsync(cancellationToken);
        if (!current.HasValue || !(Owned(current!, reservationId, "pending") || Owned(current!, reservationId, "unknown"))) throw new InvalidOperationException("Upload reservation ownership was lost.");
        var completed = Serialize(new Envelope(fingerprint, reservationId, "completed", response, Deserialize(current!).EffectivePath));
        var envelope = Deserialize(current!);
        var transaction = database.CreateTransaction(); transaction.AddCondition(Condition.StringEqual(key, current));
        if (envelope.State == "pending") transaction.AddCondition(Condition.StringEqual(leaseKey, reservationId));
        _ = transaction.StringSetAsync(key, completed, Retention); _ = transaction.KeyDeleteAsync(leaseKey);
        if (!await transaction.ExecuteAsync().WaitAsync(cancellationToken)) throw new InvalidOperationException("Upload reservation changed before completion.");
    }

    /// <inheritdoc />
    public async Task MarkUnknownAsync(string identity, string reservationId, UploadResultResponse? response, CancellationToken cancellationToken)
    {
        var database = Database(); var key = Key(identity); var leaseKey = LeaseKey(identity); var current = await database.StringGetAsync(key).WaitAsync(cancellationToken);
        if (!current.HasValue || !Owned(current!, reservationId, "pending")) return;
        var envelope = Deserialize(current!);
        var transaction = database.CreateTransaction(); transaction.AddCondition(Condition.StringEqual(key, current));
        _ = transaction.StringSetAsync(key, Serialize(envelope with { State = "unknown", Response = response }), Retention); _ = transaction.KeyDeleteAsync(leaseKey);
        _ = await transaction.ExecuteAsync().WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(string identity, string reservationId, CancellationToken cancellationToken)
    {
        var database = Database(); var key = Key(identity); var leaseKey = LeaseKey(identity); var current = await database.StringGetAsync(key).WaitAsync(cancellationToken);
        if (!current.HasValue || !Owned(current!, reservationId, "pending")) return;
        var transaction = database.CreateTransaction(); transaction.AddCondition(Condition.StringEqual(key, current)); transaction.AddCondition(Condition.StringEqual(leaseKey, reservationId)); _ = transaction.KeyDeleteAsync(key); _ = transaction.KeyDeleteAsync(leaseKey);
        _ = await transaction.ExecuteAsync().WaitAsync(cancellationToken);
    }

    private static async Task<bool> ReplaceAsync(IDatabase database, RedisKey key, RedisValue current, RedisValue replacement, CancellationToken cancellationToken)
    {
        var transaction = database.CreateTransaction(); transaction.AddCondition(Condition.StringEqual(key, current)); _ = transaction.StringSetAsync(key, replacement, Retention);
        return await transaction.ExecuteAsync().WaitAsync(cancellationToken);
    }
    private IDatabase Database() => redis?.GetDatabase() ?? throw new InvalidOperationException("Redis is required for upload replay protection.");
    private static RedisKey Key(string identity) => $"legacy:file:idempotency:v1:{identity}";
    private static RedisKey LeaseKey(string identity) => $"legacy:file:idempotency:v1:{identity}:lease";
    private static bool Owned(RedisValue value, string reservation, string state) { var e = Deserialize(value); return e.ReservationId == reservation && e.State == state; }
    private static RedisValue Serialize(Envelope envelope) => JsonSerializer.Serialize(envelope, JsonOptions);
    private static Envelope Deserialize(RedisValue value) => JsonSerializer.Deserialize<Envelope>((string)value!, JsonOptions) ?? throw new InvalidDataException("Upload checkpoint is invalid.");
    private sealed record Envelope(string Fingerprint, string ReservationId, string State, UploadResultResponse? Response, string EffectivePath);
}
