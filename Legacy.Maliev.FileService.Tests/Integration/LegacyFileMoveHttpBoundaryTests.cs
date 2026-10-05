using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google;
using Google.Cloud.Storage.V1;
using Legacy.Maliev.FileService.Api.Authorization;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Models;
using Legacy.Maliev.FileService.Application.Services;
using Legacy.Maliev.FileService.Data;
using Legacy.Maliev.FileService.Domain;
using Maliev.Aspire.ServiceDefaults.IAM;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Moq;
using StorageObject = Google.Apis.Storage.v1.Data.Object;

namespace Legacy.Maliev.FileService.Tests.Integration;

// Actual admitted PUT requests; SDK effects are controlled, application and journals are real.
[Collection(LegacyMovePostgreSqlCollection.Name)]
public sealed class LegacyFileMoveHttpBoundaryTests(PostgreSqlFixture fixture)
{
    private const string Source = "orders/ต้นฉบับ แรก.stl";
    private const string Destination = "orders/ปลายทาง สอง.stl";

    [Fact]
    public async Task EncodedCoordinates_MoveExactCommittedGenerationAndMetadataThroughActual204()
    {
        await using var context = await ContextAsync();
        await using var factory = new MoveFactory(context.Database.GetConnectionString()!);
        var proofId = await SeedAsync(context, factory);
        using var client = factory.Client();
        using var response = await SendAsync(client, factory.Token());
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
        Assert.False(factory.Objects.ContainsKey(Source));
        Assert.Equal(47L, factory.Objects[Destination].Generation);
        var metadata = Assert.Single(await context.Uploads.AsNoTracking().ToArrayAsync());
        Assert.Equal(Destination, metadata.Name);
        Assert.Equal(3L, metadata.Size);
        var move = Assert.Single(await context.StorageMoveJournals.AsNoTracking().Where(row => row.OperationId != proofId).ToArrayAsync());
        Assert.Equal(Source, move.SourceObjectName);
        Assert.Equal(Destination, move.DestinationObjectName);
        Assert.Equal(31L, move.SourceGeneration);
        Assert.Equal(47L, move.DestinationGeneration);
        Assert.True(move.ScanClean);
        Assert.Equal("MetadataCommitted", move.State);
        var wire = Assert.Single(factory.Wire.Responses);
        Assert.Equal(HttpMethod.Put, wire.Method);
        Assert.Contains(Uri.EscapeDataString(Source), wire.Uri.Query, StringComparison.Ordinal);
        Assert.Contains(Uri.EscapeDataString(Destination), wire.Uri.Query, StringComparison.Ordinal);
        Assert.Equal(31L, Assert.Single(factory.Deletes).Generation);
        Assert.Equal(1, factory.CopyCalls);
        await AssertPriorProofAsync(context, proofId);
        AssertRealRuntime(factory);
    }

    [Theory]
    [InlineData("sourceBucket", false)]
    [InlineData("sourceBucket", true)]
    [InlineData("sourceObjectName", false)]
    [InlineData("sourceObjectName", true)]
    [InlineData("destinationBucket", false)]
    [InlineData("destinationBucket", true)]
    [InlineData("destinationObjectName", false)]
    [InlineData("destinationObjectName", true)]
    public async Task EachMissingOrEmptyField_Actual400PreservesExistingSourceWithoutSdk(string field, bool empty)
    {
        await using var context = await ContextAsync();
        await using var factory = new MoveFactory(context.Database.GetConnectionString()!);
        var proofId = await SeedAsync(context, factory);
        var values = Coordinates();
        if (empty) values[field] = "";
        else values.Remove(field);
        using var client = factory.Client();
        using var response = await SendAsync(client, factory.Token(), values);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Bucket and object names are required", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await AssertUntouchedAsync(context, factory, proofId);
    }

    [Theory]
    [InlineData("sourceBucket", "unapproved.example")]
    [InlineData("destinationBucket", "unapproved.example")]
    [InlineData("sourceObjectName", "orders/../secret.stl")]
    [InlineData("destinationObjectName", "orders/../secret.stl")]
    public async Task InvalidCoordinates_Actual400BeforeAnySdkOrMoveCheckpoint(string field, string value)
    {
        await using var context = await ContextAsync();
        await using var factory = new MoveFactory(context.Database.GetConnectionString()!);
        var proofId = await SeedAsync(context, factory);
        var values = Coordinates(); values[field] = value;
        using var client = factory.Client();
        using var response = await SendAsync(client, factory.Token(), values);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertUntouchedAsync(context, factory, proofId);
    }

    [Theory]
    [InlineData("anonymous", 401)]
    [InlineData("wrong-key", 401)]
    [InlineData("denied", 403)]
    public async Task CompleteEnabledMove_ActualSignedAdmissionRejectsBeforeProvider(string identity, int status)
    {
        await using var context = await ContextAsync();
        await using var factory = new MoveFactory(context.Database.GetConnectionString()!);
        var proofId = await SeedAsync(context, factory);
        using var client = factory.Client();
        using var response = await SendAsync(client, factory.Token(identity));
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        await AssertUntouchedAsync(context, factory, proofId);
    }

    [Theory]
    [InlineData("copy-rejected", true, false)]
    [InlineData("copy-unknown", true, true)]
    [InlineData("delete-rejected", true, false)]
    [InlineData("delete-unknown", false, true)]
    public async Task ProviderFailure_ActualOpaque503RetainsExactObjectsAndUnknownCheckpoint(string fault, bool sourceRemains, bool destinationRemains)
    {
        await using var context = await ContextAsync();
        await using var factory = new MoveFactory(context.Database.GetConnectionString()!, fault);
        var proofId = await SeedAsync(context, factory);
        using var client = factory.Client();
        using var response = await SendAsync(client, factory.Token());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("synthetic", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Source, text, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(text);
        Assert.Equal(503, json.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("Move outcome unknown", json.RootElement.GetProperty("title").GetString());
        Assert.Equal(sourceRemains, factory.Objects.ContainsKey(Source));
        Assert.Equal(destinationRemains, factory.Objects.ContainsKey(Destination));
        Assert.Equal(Source, Assert.Single(await context.Uploads.AsNoTracking().ToArrayAsync()).Name);
        var move = Assert.Single(await context.StorageMoveJournals.AsNoTracking().Where(row => row.OperationId != proofId).ToArrayAsync());
        Assert.Equal("Unknown", move.State);
        Assert.Equal(31L, move.SourceGeneration);
        Assert.Equal(1, factory.CopyCalls);
        if (fault == "delete-rejected") Assert.Collection(factory.Deletes,
            item => Assert.Equal(31L, item.Generation), item => Assert.Equal(47L, item.Generation));
        else if (fault == "delete-unknown") Assert.Equal(31L, Assert.Single(factory.Deletes).Generation);
        else Assert.Empty(factory.Deletes);
        await AssertPriorProofAsync(context, proofId);
        AssertRealRuntime(factory);
    }

    [Theory]
    [InlineData("wait-copy")]
    [InlineData("wait-delete")]
    public async Task CallerAbort_ActualSdkTokenCancelsWithoutRepeatingMove(string fault)
    {
        await using var context = await ContextAsync();
        await using var factory = new MoveFactory(context.Database.GetConnectionString()!, fault);
        var proofId = await SeedAsync(context, factory);
        using var client = factory.Client();
        using var cancellation = new CancellationTokenSource();
        var send = SendAsync(client, factory.Token(), token: cancellation.Token);
        try
        {
            await factory.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send.WaitAsync(TimeSpan.FromSeconds(10)));
            await factory.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, factory.CopyCalls);
            Assert.Equal(fault == "wait-delete" ? 1 : 0, factory.Deletes.Count);
            Assert.Equal(Source, Assert.Single(await context.Uploads.AsNoTracking().ToArrayAsync()).Name);
            Assert.True(factory.Objects.ContainsKey(Source));
            Assert.Equal(fault == "wait-delete", factory.Objects.ContainsKey(Destination));
            await AssertPriorProofAsync(context, proofId);
        }
        finally
        {
            cancellation.Cancel();
            try { using var response = await send.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch { /* Preserve the original assertion and bound caller cleanup. */ }
        }
        AssertRealRuntime(factory);
    }

    private static Dictionary<string, string> Coordinates() => new()
    {
        ["sourceBucket"] = "maliev.com",
        ["sourceObjectName"] = Source,
        ["destinationBucket"] = "maliev.com",
        ["destinationObjectName"] = Destination,
    };

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string tokenValue,
        Dictionary<string, string>? coordinates = null, CancellationToken token = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "/Uploads?" + string.Join("&", (coordinates ?? Coordinates())
            .Select(item => $"{item.Key}={Uri.EscapeDataString(item.Value)}")));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokenValue);
        return await client.SendAsync(request, token);
    }

    private static async Task<Guid> SeedAsync(FileDbContext context, MoveFactory factory)
    {
        context.Uploads.Add(new Upload { Bucket = "maliev.com", Name = Source, Size = 3, ContentType = "model/stl" });
        await context.SaveChangesAsync();
        var id = Guid.NewGuid();
        var journal = new StorageMoveJournalRepository(context, TimeProvider.System);
        Assert.True(await journal.BeginAsync(id, true, "maliev.com", "_quarantine/source-" + id.ToString("N"), 17,
            "maliev.com", Source, default));
        await journal.CopiedAsync(id, 31, default);
        await journal.SourceDeletedAsync(id, default);
        await journal.MetadataCommittedAsync(id, default);
        factory.Objects.Add(Source, (31, [0, 255, 1]));
        return id;
    }

    private static async Task AssertPriorProofAsync(FileDbContext context, Guid id)
    {
        var row = await context.StorageMoveJournals.AsNoTracking().SingleAsync(value => value.OperationId == id);
        Assert.Equal("MetadataCommitted", row.State);
        Assert.Equal(17L, row.SourceGeneration);
        Assert.Equal(31L, row.DestinationGeneration);
        Assert.Equal(Source, row.DestinationObjectName);
        Assert.True(row.ScanClean);
    }

    private static async Task AssertUntouchedAsync(FileDbContext context, MoveFactory factory, Guid proofId)
    {
        Assert.Equal(0, factory.GetCalls);
        Assert.Equal(0, factory.CopyCalls);
        Assert.Empty(factory.Deletes);
        Assert.Equal(31L, Assert.Single(factory.Objects).Value.Generation);
        Assert.Equal(Source, Assert.Single(await context.Uploads.AsNoTracking().ToArrayAsync()).Name);
        Assert.Single(await context.StorageMoveJournals.AsNoTracking().ToArrayAsync());
        await AssertPriorProofAsync(context, proofId);
        AssertRealRuntime(factory);
    }

    private async Task<FileDbContext> ContextAsync()
    {
        var context = fixture.CreateContext();
        await context.Database.MigrateAsync();
        // This collection owns a separate container and serializes its cases; reset only its fixture rows.
        await context.QuarantineUploadIntents.ExecuteDeleteAsync();
        await context.StorageMoveJournals.ExecuteDeleteAsync();
        await context.Uploads.ExecuteDeleteAsync();
        return context;
    }

    private static void AssertRealRuntime(MoveFactory factory)
    {
        Assert.Null(factory.Services.GetService<IIamServiceClient>());
        using var scope = factory.Services.CreateScope();
        Assert.IsType<FileApplicationService>(scope.ServiceProvider.GetRequiredService<IFileService>());
        Assert.IsType<GoogleCloudObjectStorage>(scope.ServiceProvider.GetRequiredService<IObjectStorage>());
        Assert.IsType<UploadRepository>(scope.ServiceProvider.GetRequiredService<IUploadRepository>());
        Assert.IsType<StorageMoveJournalRepository>(scope.ServiceProvider.GetRequiredService<IStorageMoveJournal>());
        Assert.Empty(factory.Scanner.Bytes);
        Assert.Equal(0, factory.SignCalls);
        Assert.Equal(0, factory.UploadCalls);
        factory.Checkpoints.VerifyNoOtherCalls();
    }

    private sealed class RecordingScanner : IFileSafetyScanner
    {
        public FileSafetyVerdict Verdict { get; set; } = FileSafetyVerdict.Clean;
        public List<byte[]> Bytes { get; } = [];
        public async Task<FileSafetyResult> ScanAsync(IUploadFile file, CancellationToken token)
        {
            await using var input = file.OpenReadStream();
            using var output = new MemoryStream();
            await input.CopyToAsync(output, token);
            Bytes.Add(output.ToArray());
            return new FileSafetyResult(Verdict);
        }
    }

    private sealed class WireObserver : DelegatingHandler
    {
        public List<(HttpMethod Method, Uri Uri, HttpStatusCode Status, string Body)> Responses { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = await base.SendAsync(request, token);
            Responses.Add((request.Method, request.RequestUri!, response.StatusCode, await response.Content.ReadAsStringAsync(token)));
            return response;
        }
    }

    private sealed class MoveFactory(string connection, string fault = "none") : WebApplicationFactory<Program>
    {
        private readonly RSA signingKey = RSA.Create(2048);
        public Mock<StorageClient> Sdk { get; } = new(MockBehavior.Strict);
        public Mock<UrlSigner.IBlobSigner> Signer { get; } = new(MockBehavior.Strict);
        public Mock<IUploadIdempotencyStore> Checkpoints { get; } = new(MockBehavior.Strict);
        public RecordingScanner Scanner { get; } = new();
        public WireObserver Wire { get; } = new();
        public Dictionary<string, (long Generation, byte[] Bytes)> Objects { get; } = [];
        public List<(string Name, long? Generation)> Deletes { get; } = [];
        public int UploadCalls { get; private set; }
        public int CopyCalls { get; private set; }
        public int SignCalls { get; private set; }
        public int GetCalls { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HttpClient Client() => CreateDefaultClient(Wire);

        public string Token(string identity = "all")
        {
            if (identity == "anonymous") return string.Empty;
            using var wrongKey = identity == "wrong-key" ? RSA.Create(2048) : null;
            var claims = new List<Claim> { new("sub", "move-fixture") };
            if (identity != "denied")
                foreach (var permission in new[] { FilePermissions.Update })
                    claims.Add(new("permissions", permission));
            var now = DateTime.UtcNow;
            return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("https://issuer.example.invalid", "https://file.example.invalid",
                claims, now.AddMinutes(-1), now.AddMinutes(5),
                new SigningCredentials(new RsaSecurityKey(wrongKey ?? signingKey), SecurityAlgorithms.RsaSha256)));
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("ConnectionStrings:FileDbContext", connection);
            builder.UseSetting("Cache:RedisEnabled", "false");
            builder.UseSetting("Jwt:PublicKey", Convert.ToBase64String(Encoding.UTF8.GetBytes(signingKey.ExportSubjectPublicKeyInfoPem())));
            builder.UseSetting("Jwt:Issuer", "https://issuer.example.invalid");
            builder.UseSetting("Jwt:Audience", "https://file.example.invalid");
            builder.UseSetting("FileStorage:Enabled", "true");
            builder.UseSetting("FileStorage:WritesEnabled", "true");
            builder.UseSetting("FileStorage:AllowedBuckets:0", "maliev.com");
            builder.ConfigureServices(services =>
            {
                ConfigureSdk();
                Signer.SetupGet(value => value.Id).Returns("controlled@example.invalid");
                Signer.SetupGet(value => value.Algorithm).Returns("GOOG4-RSA-SHA256");
                Signer.Setup(value => value.CreateSignatureAsync(It.IsAny<byte[]>(), It.IsAny<UrlSigner.BlobSignerParameters>(), It.IsAny<CancellationToken>()))
                    .Returns(() =>
                    {
                        SignCalls++;
                        return Task.FromException<string>(new InvalidOperationException("Move must not sign."));
                    });
                services.RemoveAll<StorageClient>(); services.AddSingleton(Sdk.Object);
                services.RemoveAll<UrlSigner>(); services.AddSingleton(UrlSigner.FromBlobSigner(Signer.Object));
                services.RemoveAll<IFileSafetyScanner>(); services.AddSingleton<IFileSafetyScanner>(Scanner);
                services.RemoveAll<IUploadIdempotencyStore>(); services.AddSingleton(Checkpoints.Object);
            });
        }

        private void ConfigureSdk()
        {
            Sdk.Setup(value => value.UploadObjectAsync(It.IsAny<StorageObject>(), It.IsAny<Stream>(), It.IsAny<UploadObjectOptions>(), It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    UploadCalls++;
                    return Task.FromException<StorageObject>(new InvalidOperationException("Move must not upload."));
                });
            Sdk.Setup(value => value.GetObjectAsync("maliev.com", It.IsAny<string>(), It.IsAny<GetObjectOptions>(), It.IsAny<CancellationToken>()))
                .Returns<string, string, GetObjectOptions, CancellationToken>((_, name, _, _) =>
                {
                    GetCalls++;
                    return Objects.TryGetValue(name, out var item)
                        ? Task.FromResult(new StorageObject { Generation = item.Generation, Size = (ulong)item.Bytes.Length })
                        : Task.FromException<StorageObject>(ApiError(HttpStatusCode.NotFound));
                });
            Sdk.Setup(value => value.CopyObjectAsync("maliev.com", It.IsAny<string>(), "maliev.com", It.IsAny<string>(), It.IsAny<CopyObjectOptions>(), It.IsAny<CancellationToken>()))
                .Returns<string, string, string, string, CopyObjectOptions, CancellationToken>(async (_, source, _, target, options, token) =>
                {
                    CopyCalls++;
                    Assert.Equal(Source, source);
                    Assert.Equal(Destination, target);
                    Assert.Equal(31L, options.SourceGeneration);
                    Assert.Equal(31L, options.IfSourceGenerationMatch);
                    Assert.Equal(0L, options.IfGenerationMatch);
                    if (fault == "wait-copy") await WaitForAbortAsync(token);
                    if (fault == "copy-rejected") throw ApiError(HttpStatusCode.Forbidden);
                    Assert.False(Objects.ContainsKey(target));
                    Objects.Add(target, (47, Objects[source].Bytes));
                    if (fault == "copy-unknown") throw new IOException("synthetic copy acknowledgment lost");
                    return new StorageObject { Generation = 47 };
                });
            Sdk.Setup(value => value.DeleteObjectAsync("maliev.com", It.IsAny<string>(), It.IsAny<DeleteObjectOptions>(), It.IsAny<CancellationToken>()))
                .Returns<string, string, DeleteObjectOptions, CancellationToken>(async (_, name, options, token) =>
                {
                    Deletes.Add((name, options.IfGenerationMatch));
                    Assert.Equal(name == Source ? 31L : 47L, options.IfGenerationMatch);
                    if (name == Source && fault == "wait-delete") await WaitForAbortAsync(token);
                    if (name == Source && fault == "delete-rejected") throw ApiError(HttpStatusCode.Forbidden);
                    if (!Objects.TryGetValue(name, out var item)) throw ApiError(HttpStatusCode.NotFound);
                    if (options.IfGenerationMatch != item.Generation) throw ApiError(HttpStatusCode.PreconditionFailed);
                    Objects.Remove(name);
                    if (name == Source && fault == "delete-unknown") throw new IOException("synthetic delete acknowledgment lost");
                });
        }

        private async Task WaitForAbortAsync(CancellationToken token)
        {
            Entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { Canceled.TrySetResult(); throw; }
            throw new InvalidOperationException("Controlled provider wait must be canceled.");
        }

        private static GoogleApiException ApiError(HttpStatusCode status) => new("storage", "synthetic provider rejection") { HttpStatusCode = status };

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) signingKey.Dispose();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class LegacyMovePostgreSqlCollection : ICollectionFixture<PostgreSqlFixture>
{
    public const string Name = "LegacyMovePostgreSQL";
}
