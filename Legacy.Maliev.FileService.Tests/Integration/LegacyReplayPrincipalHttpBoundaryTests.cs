using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Cloud.Storage.V1;
using Legacy.Maliev.FileService.Api.Authorization;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Services;
using Legacy.Maliev.FileService.Data;
using Maliev.Aspire.ServiceDefaults.IAM;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Moq;
using StackExchange.Redis;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Legacy.Maliev.FileService.Application.Models;

namespace Legacy.Maliev.FileService.Tests.Integration;

// Replay authority uses real signed admission and a persisted Redis checkpoint.
[Collection(LegacyReplayPostgreSqlCollection.Name)]
public sealed class LegacyReplayPrincipalHttpBoundaryTests(PostgreSqlFixture fixture) : IAsyncLifetime
{
    private readonly IContainer redisContainer = new ContainerBuilder("redis:8-alpine")
        .WithPortBinding(6379, true).WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379)).Build();
    private IConnectionMultiplexer? redis;

    public async Task InitializeAsync()
    {
        await redisContainer.StartAsync();
        redis = await ConnectionMultiplexer.ConnectAsync($"{redisContainer.Hostname}:{redisContainer.GetMappedPublicPort(6379)},abortConnect=false");
    }

    public async Task DisposeAsync()
    {
        if (redis is not null) { await redis.CloseAsync(); redis.Dispose(); }
        await redisContainer.DisposeAsync();
    }

    [Theory]
    [InlineData("client_id", " service-a")]
    [InlineData("client_id", "service-a ")]
    [InlineData("azp", " service-a")]
    [InlineData("azp", "service-a ")]
    [InlineData("sub", " service-a")]
    [InlineData("sub", "service-a ")]
    [InlineData("sub", "service\na")]
    [InlineData("client_id", "service\ta")]
    public async Task NoncanonicalSelectedSignedPrincipal_RejectsBeforeReplayingCanonicalCheckpoint(string claim, string value)
    {
        await using var context = fixture.CreateContext();
        await context.Database.MigrateAsync();
        await using var factory = new MultipartFactory(context.Database.GetConnectionString()!, redis!);
        var identity = await SeedAsync(factory);
        var before = await redis!.GetDatabase().StringGetAsync($"legacy:file:idempotency:v1:{identity}");
        using var client = factory.Client(claim, value);
        using var body = Multipart();
        using var response = await client.PostAsync("/Uploads?bucket=private&path=orders", body);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("canonical-only", text, StringComparison.Ordinal);
        Assert.DoesNotContain(value, text, StringComparison.Ordinal);
        Assert.Equal(0, factory.CheckpointCalls);
        Assert.Equal(before, await redis.GetDatabase().StringGetAsync($"legacy:file:idempotency:v1:{identity}"));
        await AssertRealBoundaryWithoutEffectsAsync(factory, context);
    }

    [Theory]
    [InlineData("client_id", "workflow-42")]
    [InlineData("azp", "workflow-42")]
    [InlineData("sub", "workflow-42")]
    [InlineData("sub", " workflow-42 ")]
    public async Task CanonicalSelectedPrincipal_ReplaysExistingHashAndExactResponseWithKeyCompatibility(string claim, string workflowKey)
    {
        await using var context = fixture.CreateContext();
        await context.Database.MigrateAsync();
        await using var factory = new MultipartFactory(context.Database.GetConnectionString()!, redis!);
        await SeedAsync(factory);
        using var client = factory.Client(claim, "service-a", workflowKey);
        using var body = Multipart();
        using var response = await client.PostAsync("/Uploads?bucket=private&path=orders", body);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("Google Cloud Storage", response.Headers.Location!.OriginalString);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("https://storage.test/signed?token=canonical-only", json.RootElement.GetProperty("Object")[0].GetProperty("Uri").GetString());
        Assert.Equal(1, factory.CheckpointCalls);
        await AssertRealBoundaryWithoutEffectsAsync(factory, context);
    }

    private static async Task<string> SeedAsync(MultipartFactory factory)
    {
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("service-a\nworkflow-42")));
        const string fingerprint = "12D43F63B6B2CF5EFFB1FACD73C54A82FF883CF6A883891DAC841616B0FDB1A4";
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<RedisUploadIdempotencyStore>();
        var owner = await store.AcquireAsync(identity, fingerprint, "orders", default);
        await store.CompleteAsync(identity, fingerprint, owner.ReservationId!,
            new UploadResultResponse([new("private", "orders/part.step", new Uri("https://storage.test/signed?token=canonical-only"))]), default);
        return identity;
    }

    private static MultipartFormDataContent Multipart()
    {
        var body = new MultipartFormDataContent();
        var file = new ByteArrayContent([1]);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/step");
        body.Add(file, "files", "part.step");
        return body;
    }

    private static async Task AssertRealBoundaryWithoutEffectsAsync(MultipartFactory factory, FileDbContext context)
    {
        Assert.Null(factory.Services.GetService<IIamServiceClient>());
        using var scope = factory.Services.CreateScope();
        Assert.IsType<FileApplicationService>(scope.ServiceProvider.GetRequiredService<IFileService>());
        Assert.IsType<GoogleCloudObjectStorage>(scope.ServiceProvider.GetRequiredService<IObjectStorage>());
        Assert.IsType<UploadRepository>(scope.ServiceProvider.GetRequiredService<IUploadRepository>());
        Assert.IsType<StorageMoveJournalRepository>(scope.ServiceProvider.GetRequiredService<IStorageMoveJournal>());
        factory.Sdk.VerifyNoOtherCalls();
        factory.Scanner.VerifyNoOtherCalls();
        factory.Signer.Verify(value => value.CreateSignatureAsync(It.IsAny<byte[]>(), It.IsAny<UrlSigner.BlobSignerParameters>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.False(await context.Uploads.AnyAsync());
        Assert.False(await context.StorageMoveJournals.AnyAsync());
        Assert.False(await context.QuarantineUploadIntents.AnyAsync());
    }

    private sealed class MultipartFactory(string connection, IConnectionMultiplexer redis) : WebApplicationFactory<Program>
    {
        private readonly RSA key = RSA.Create(2048);
        public Mock<StorageClient> Sdk { get; } = new(MockBehavior.Strict);
        public Mock<IFileSafetyScanner> Scanner { get; } = new(MockBehavior.Strict);
        public int CheckpointCalls { get; private set; }
        public Mock<UrlSigner.IBlobSigner> Signer { get; } = new(MockBehavior.Strict);

        public HttpClient Client(string claim, string value, string workflowKey = "workflow-42")
        {
            var client = CreateClient();
            var claims = new List<Claim> { new("permissions", FilePermissions.Create) };
            if (claim != "sub") claims.Add(new("sub", "different-fallback-subject"));
            if (claim == "client_id") claims.Add(new("azp", "different-fallback-azp"));
            claims.Add(new(claim, value));
            var now = DateTime.UtcNow;
            var token = new JwtSecurityToken("https://issuer.example.invalid", "https://file.example.invalid", claims,
                now.AddMinutes(-1), now.AddMinutes(5), new SigningCredentials(new RsaSecurityKey(key), SecurityAlgorithms.RsaSha256));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
            client.DefaultRequestHeaders.Add("Idempotency-Key", workflowKey);
            return client;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("ConnectionStrings:FileDbContext", connection);
            builder.UseSetting("Cache:RedisEnabled", "false");
            builder.UseSetting("Jwt:PublicKey", Convert.ToBase64String(Encoding.UTF8.GetBytes(key.ExportSubjectPublicKeyInfoPem())));
            builder.UseSetting("Jwt:Issuer", "https://issuer.example.invalid");
            builder.UseSetting("Jwt:Audience", "https://file.example.invalid");
            builder.UseSetting("FileStorage:Enabled", "true");
            builder.UseSetting("FileStorage:WritesEnabled", "true");
            builder.UseSetting("FileStorage:AllowedBuckets:0", "private");
            builder.ConfigureServices(services =>
            {
                Signer.SetupGet(value => value.Id).Returns("controlled@example.invalid");
                Signer.SetupGet(value => value.Algorithm).Returns("GOOG4-RSA-SHA256");
                services.RemoveAll<StorageClient>();
                services.AddSingleton(Sdk.Object);
                services.RemoveAll<UrlSigner>();
                services.AddSingleton(UrlSigner.FromBlobSigner(Signer.Object));
                services.RemoveAll<IFileSafetyScanner>();
                services.AddSingleton(Scanner.Object);
                services.RemoveAll<IUploadIdempotencyStore>();
                services.AddSingleton(redis);
                services.AddScoped<RedisUploadIdempotencyStore>();
                services.AddScoped<IUploadIdempotencyStore>(provider => new ObservedStore(
                    provider.GetRequiredService<RedisUploadIdempotencyStore>(), () => CheckpointCalls++));
            });
        }

        // Observe calls while forwarding every operation to the actual Redis store unchanged.
        private sealed class ObservedStore(RedisUploadIdempotencyStore inner, Action called) : IUploadIdempotencyStore
        {
            public Task<UploadAcquireResult> AcquireAsync(string identity, string fingerprint, string path, CancellationToken token)
            { called(); return inner.AcquireAsync(identity, fingerprint, path, token); }
            public Task<bool> RenewAsync(string identity, string reservation, CancellationToken token)
            { called(); return inner.RenewAsync(identity, reservation, token); }
            public Task CompleteAsync(string identity, string fingerprint, string reservation, UploadResultResponse response, CancellationToken token)
            { called(); return inner.CompleteAsync(identity, fingerprint, reservation, response, token); }
            public Task MarkUnknownAsync(string identity, string reservation, UploadResultResponse? response, CancellationToken token)
            { called(); return inner.MarkUnknownAsync(identity, reservation, response, token); }
            public Task ReleaseAsync(string identity, string reservation, CancellationToken token)
            { called(); return inner.ReleaseAsync(identity, reservation, token); }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) key.Dispose();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class LegacyReplayPostgreSqlCollection : ICollectionFixture<PostgreSqlFixture>
{
    public const string Name = "LegacyReplayPostgreSQL";
}
