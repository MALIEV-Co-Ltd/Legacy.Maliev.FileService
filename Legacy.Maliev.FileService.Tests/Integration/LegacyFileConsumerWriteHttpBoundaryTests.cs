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
using LegacyFileClient = Legacy.Maliev.Intranet.PurchaseOrders.LegacyFileClient;
using StorageObject = Google.Apis.Storage.v1.Data.Object;

namespace Legacy.Maliev.FileService.Tests.Integration;

// Executes the immutable linked Intranet consumer; only SDK/scanner/signing effects are controlled.
[Collection(LegacyConsumerWritePostgreSqlCollection.Name)]
public sealed class LegacyFileConsumerWriteHttpBoundaryTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task PdfConsumer_ParsesOnePascalCaseCreatedObjectAfterExactBytesAndDurablePromotion()
    {
        await using var context = await ContextAsync();
        await using var factory = new ConsumerFactory(context.Database.GetConnectionString()!);
        using var client = factory.Client();
        var before = DateTime.UtcNow;
        var result = await new LegacyFileClient(client).UploadPdfAsync(42, [0, 255, 1], factory.Token(), default);
        Assert.Equal("maliev.com", result.Bucket);
        var dates = new[] { before.ToString("yyyy/MM/dd", System.Globalization.CultureInfo.InvariantCulture), DateTime.UtcNow.ToString("yyyy/MM/dd", System.Globalization.CultureInfo.InvariantCulture) };
        Assert.Contains(dates, date => result.ObjectName == $"purchaseorders/{date}/purchaseorder_42.pdf");
        Assert.Equal("storage.googleapis.com", result.Uri.Host);
        var uploaded = Assert.Single(factory.Uploads);
        Assert.Equal("application/pdf", uploaded.ContentType);
        Assert.Equal(new byte[] { 0, 255, 1 }, uploaded.Bytes);
        Assert.Equal(uploaded.Bytes, Assert.Single(factory.Scanner.Bytes));
        var row = Assert.Single(await context.Uploads.AsNoTracking().ToArrayAsync());
        Assert.Equal(result.ObjectName, row.Name);
        Assert.Equal(3L, row.Size);
        await AssertCommittedPromotionsAsync(factory, context, 1);
    }

    [Fact]
    public async Task OrderConsumer_FiltersEmptyFilesPreservesOrderAndFallsBackToBinaryMediaType()
    {
        await using var context = await ContextAsync();
        await using var factory = new ConsumerFactory(context.Database.GetConnectionString()!);
        using var client = factory.Client();
        var before = DateTime.UtcNow;
        var result = await new LegacyFileClient(client).UploadOrderFilesAsync(42,
            [File("First.STL", "model/stl", [1, 2]), File("ignored.txt", "text/plain", []), File("ชิ้นงาน.STEP", "not@a/media type", [0, 255, 3])], factory.Token(), default);
        var dates = new[] { before.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) };
        Assert.Collection(result,
            item => Assert.Contains(dates, date => item.ObjectName == $"uploads/42/{date}/first.stl"),
            item => Assert.Contains(dates, date => item.ObjectName == $"uploads/42/{date}/ชิ้นงาน.step"));
        Assert.Collection(factory.Uploads,
            item => { Assert.Equal("model/stl", item.ContentType); Assert.Equal(new byte[] { 1, 2 }, item.Bytes); },
            item => { Assert.Equal("application/octet-stream", item.ContentType); Assert.Equal(new byte[] { 0, 255, 3 }, item.Bytes); });
        Assert.Collection(factory.Scanner.Bytes,
            bytes => Assert.Equal(new byte[] { 1, 2 }, bytes), bytes => Assert.Equal(new byte[] { 0, 255, 3 }, bytes));
        Assert.Equal(2, await context.Uploads.CountAsync());
        await AssertCommittedPromotionsAsync(factory, context, 2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrderConsumer_EmptyCollectionReturnsLocallyButAllEmptyPartsReachLegacy400(bool allEmpty)
    {
        await using var context = await ContextAsync();
        await using var factory = new ConsumerFactory(context.Database.GetConnectionString()!);
        using var client = factory.Client();
        var consumer = new LegacyFileClient(client);
        if (allEmpty)
        {
            var error = await Assert.ThrowsAsync<HttpRequestException>(() => consumer.UploadOrderFilesAsync(42, [File(bytes: [])], factory.Token(), default));
            Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
            Assert.Single(factory.Wire.Responses);
        }
        else
        {
            Assert.Empty(await consumer.UploadOrderFilesAsync(42, [], factory.Token(), default));
            Assert.Empty(factory.Wire.Responses);
        }
        Assert.Equal(0, factory.UploadCalls);
        Assert.Empty(factory.Scanner.Bytes);
        Assert.Equal(0, factory.SignCalls);
        await AssertNoMetadataAsync(context);
        AssertRealRuntime(factory);
    }

    [Theory]
    [InlineData("pdf", "anonymous", 401)]
    [InlineData("pdf", "wrong-key", 401)]
    [InlineData("pdf", "denied", 403)]
    [InlineData("order", "anonymous", 401)]
    [InlineData("order", "wrong-key", 401)]
    [InlineData("order", "denied", 403)]
    public async Task UploadConsumer_ActualAdmissionFailureThrowsBeforeStorageOrPersistence(string method, string identity, int status)
    {
        await using var context = await ContextAsync();
        await using var factory = new ConsumerFactory(context.Database.GetConnectionString()!);
        using var client = factory.Client();
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => InvokeUploadAsync(method, new LegacyFileClient(client), factory.Token(identity)));
        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.Equal(0, factory.UploadCalls);
        Assert.Empty(factory.Scanner.Bytes);
        Assert.Equal(0, factory.SignCalls);
        await AssertNoMetadataAsync(context);
        AssertRealRuntime(factory);
    }

    [Theory]
    [InlineData("pdf", FileSafetyVerdict.Infected, 422)]
    [InlineData("order", FileSafetyVerdict.Infected, 422)]
    [InlineData("pdf", FileSafetyVerdict.Unavailable, 503)]
    [InlineData("order", FileSafetyVerdict.Unavailable, 503)]
    public async Task UploadConsumer_ScanRejectionThrowsAndCleansOnlyPrivateQuarantine(string method, FileSafetyVerdict verdict, int status)
    {
        await using var context = await ContextAsync();
        await using var factory = new ConsumerFactory(context.Database.GetConnectionString()!);
        factory.Scanner.Verdict = verdict;
        using var client = factory.Client();
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => InvokeUploadAsync(method, new LegacyFileClient(client), factory.Token()));
        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.Equal(1, factory.UploadCalls);
        Assert.Single(factory.Scanner.Bytes);
        Assert.Equal(0, factory.CopyCalls);
        Assert.Equal(0, factory.SignCalls);
        Assert.Equal(17L, Assert.Single(factory.Deletes).Generation);
        Assert.Empty(factory.Objects);
        await AssertNoMetadataAsync(context);
        AssertRealRuntime(factory);
    }

    [Theory]
    [InlineData("pdf", "sign")]
    [InlineData("order", "sign")]
    [InlineData("pdf", "unknown-upload")]
    [InlineData("order", "unknown-upload")]
    public async Task UploadConsumer_UncertainOutcomeNeverBecomesAParsedSuccess(string method, string fault)
    {
        await using var context = await ContextAsync();
        await using var factory = new ConsumerFactory(context.Database.GetConnectionString()!, fault);
        using var client = factory.Client();
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => InvokeUploadAsync(method, new LegacyFileClient(client), factory.Token()));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
        await AssertNoMetadataAsync(context);
        Assert.Single(await context.QuarantineUploadIntents.AsNoTracking().ToArrayAsync());
        if (fault == "sign")
        {
            Assert.Equal(1, factory.CopyCalls);
            Assert.Equal(1, factory.SignCalls);
            Assert.Empty(factory.Objects);
            Assert.Equal("CompensatedRemoved", Assert.Single(await context.StorageMoveJournals.AsNoTracking().ToArrayAsync()).State);
        }
        else
        {
            Assert.Equal(0, factory.CopyCalls);
            Assert.Equal(0, factory.SignCalls);
            Assert.Single(factory.Objects);
            Assert.False(await context.StorageMoveJournals.AnyAsync());
        }
        AssertRealRuntime(factory);
    }

    [Fact]
    public async Task DeleteConsumer_204RemovesMetadataThenSignedReadReturnsNullAndRepeatDeleteThrows400()
    {
        await using var context = await ContextAsync();
        await using var factory = new ConsumerFactory(context.Database.GetConnectionString()!);
        await SeedDeleteAsync(context, factory);
        using var client = factory.Client();
        var consumer = new LegacyFileClient(client);
        await consumer.DeleteAsync("maliev.com", "orders/persisted.pdf", factory.Token(), default);
        await AssertNoMetadataAsync(context);
        Assert.Empty(factory.Objects);
        Assert.Null(await consumer.GetSignedUrlAsync("maliev.com", "orders/persisted.pdf", factory.Token(), default));
        var repeat = await Assert.ThrowsAsync<HttpRequestException>(() => consumer.DeleteAsync("maliev.com", "orders/persisted.pdf", factory.Token(), default));
        Assert.Equal(HttpStatusCode.BadRequest, repeat.StatusCode);
        Assert.Equal(0, factory.GetCalls);
        Assert.Single(factory.Deletes);
        Assert.Collection(factory.Wire.Responses,
            wire => Assert.Equal(HttpStatusCode.NoContent, wire.Status),
            wire => Assert.Equal(HttpStatusCode.NotFound, wire.Status),
            wire => Assert.Equal(HttpStatusCode.BadRequest, wire.Status));
        AssertRealRuntime(factory);
    }

    [Fact]
    public async Task DeleteConsumer_MissingMetadataThrowsLegacy400WithoutSdkEffect()
    {
        await using var context = await ContextAsync();
        await using var factory = new ConsumerFactory(context.Database.GetConnectionString()!);
        using var client = factory.Client();
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => new LegacyFileClient(client).DeleteAsync("maliev.com", "orders/missing.pdf", factory.Token(), default));
        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.Empty(factory.Deletes);
        AssertRealRuntime(factory);
    }

    [Theory]
    [InlineData("anonymous", 401)]
    [InlineData("wrong-key", 401)]
    [InlineData("denied", 403)]
    public async Task DeleteConsumer_ActualAdmissionPreservesMetadataAndNeverCallsSdk(string identity, int status)
    {
        await using var context = await ContextAsync();
        await using var factory = new ConsumerFactory(context.Database.GetConnectionString()!);
        await SeedDeleteAsync(context, factory);
        using var client = factory.Client();
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => new LegacyFileClient(client).DeleteAsync("maliev.com", "orders/persisted.pdf", factory.Token(identity), default));
        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.True(await context.Uploads.AnyAsync());
        Assert.Empty(factory.Deletes);
        Assert.Single(factory.Objects);
        AssertRealRuntime(factory);
    }

    [Fact]
    public async Task DeleteConsumer_ProviderRejectionThrowsOpaque500AndPreservesMetadata()
    {
        await using var context = await ContextAsync();
        await using var factory = new ConsumerFactory(context.Database.GetConnectionString()!, "forbidden-delete");
        await SeedDeleteAsync(context, factory);
        using var client = factory.Client();
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => new LegacyFileClient(client).DeleteAsync("maliev.com", "orders/persisted.pdf", factory.Token(), default));
        Assert.Equal(HttpStatusCode.InternalServerError, error.StatusCode);
        Assert.True(await context.Uploads.AnyAsync());
        Assert.Single(factory.Objects);
        Assert.DoesNotContain("synthetic provider rejection", Assert.Single(factory.Wire.Responses).Body, StringComparison.Ordinal);
        AssertRealRuntime(factory);
    }

    [Theory]
    [InlineData("pdf", "wait-upload")]
    [InlineData("order", "wait-upload")]
    [InlineData("delete", "wait-delete")]
    public async Task WriteConsumer_CallerAbortReachesSdkWithoutAnotherExecution(string method, string fault)
    {
        await using var context = await ContextAsync();
        await using var factory = new ConsumerFactory(context.Database.GetConnectionString()!, fault);
        if (method == "delete") await SeedDeleteAsync(context, factory);
        using var client = factory.Client();
        using var cancellation = new CancellationTokenSource();
        var consumer = new LegacyFileClient(client);
        var send = method == "delete"
            ? consumer.DeleteAsync("maliev.com", "orders/persisted.pdf", factory.Token(), cancellation.Token)
            : InvokeUploadAsync(method, consumer, factory.Token(), cancellation.Token);
        try
        {
            await factory.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send.WaitAsync(TimeSpan.FromSeconds(10)));
            await factory.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (method == "delete") { Assert.Single(factory.Deletes); Assert.True(await context.Uploads.AnyAsync()); }
            else { Assert.Equal(1, factory.UploadCalls); await AssertNoMetadataAsync(context); }
            Assert.Empty(factory.Scanner.Bytes);
            Assert.Equal(0, factory.SignCalls);
        }
        finally
        {
            cancellation.Cancel();
            try { await send.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch { /* Keep the original assertion failure and bound cleanup. */ }
        }
        AssertRealRuntime(factory);
    }

    private static async Task SeedDeleteAsync(FileDbContext context, ConsumerFactory factory)
    {
        context.Uploads.Add(new Upload { Bucket = "maliev.com", Name = "orders/persisted.pdf", Size = 3, ContentType = "application/pdf" });
        await context.SaveChangesAsync();
        factory.Objects.Add("orders/persisted.pdf", (31, [0, 255, 1]));
    }

    private static async Task AssertCommittedPromotionsAsync(ConsumerFactory factory, FileDbContext context, int count)
    {
        var wire = Assert.Single(factory.Wire.Responses);
        Assert.Equal(HttpMethod.Post, wire.Method);
        Assert.Equal(HttpStatusCode.Created, wire.Status);
        using var json = JsonDocument.Parse(wire.Body);
        Assert.False(json.RootElement.TryGetProperty("object", out _));
        Assert.Equal(count, json.RootElement.GetProperty("Object").GetArrayLength());
        foreach (var item in json.RootElement.GetProperty("Object").EnumerateArray())
        {
            Assert.Equal("maliev.com", item.GetProperty("Bucket").GetString());
            Assert.NotNull(item.GetProperty("ObjectName").GetString());
            Assert.NotNull(item.GetProperty("Uri").GetString());
        }
        Assert.Equal(count, factory.CopyCalls);
        Assert.Equal(count, factory.SignCalls);
        var journals = await context.StorageMoveJournals.AsNoTracking().ToArrayAsync();
        Assert.Equal(count, journals.Length);
        Assert.All(journals, row => { Assert.Equal("MetadataCommitted", row.State); Assert.True(row.ScanClean); Assert.Equal(17L, row.SourceGeneration); Assert.Equal(31L, row.DestinationGeneration); });
        Assert.Equal(count, await context.QuarantineUploadIntents.CountAsync());
        Assert.All(factory.Deletes, item => Assert.Equal(17L, item.Generation));
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

    private static IFormFile File(string name = "Part.STL", string contentType = "model/stl", byte[]? bytes = null)
    {
        bytes ??= [0, 255, 1];
        return new FormFile(new MemoryStream(bytes, writable: false), 0, bytes.Length, "files", name)
        { Headers = new HeaderDictionary(), ContentType = contentType };
    }

    private static async Task InvokeUploadAsync(string method, LegacyFileClient consumer, string token, CancellationToken cancellationToken = default)
    {
        if (method == "pdf") await consumer.UploadPdfAsync(42, [0, 255, 1], token, cancellationToken);
        else await consumer.UploadOrderFilesAsync(42, [File()], token, cancellationToken);
    }

    private static async Task AssertNoMetadataAsync(FileDbContext context) => Assert.False(await context.Uploads.AsNoTracking().AnyAsync());

    private static void AssertRealRuntime(ConsumerFactory factory)
    {
        Assert.Null(factory.Services.GetService<IIamServiceClient>());
        using var scope = factory.Services.CreateScope();
        Assert.IsType<FileApplicationService>(scope.ServiceProvider.GetRequiredService<IFileService>());
        Assert.IsType<GoogleCloudObjectStorage>(scope.ServiceProvider.GetRequiredService<IObjectStorage>());
        Assert.IsType<UploadRepository>(scope.ServiceProvider.GetRequiredService<IUploadRepository>());
        Assert.IsType<StorageMoveJournalRepository>(scope.ServiceProvider.GetRequiredService<IStorageMoveJournal>());
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

    private sealed class ConsumerFactory(string connection, string fault = "none") : WebApplicationFactory<Program>
    {
        private readonly RSA signingKey = RSA.Create(2048);
        public Mock<StorageClient> Sdk { get; } = new(MockBehavior.Strict);
        public Mock<UrlSigner.IBlobSigner> Signer { get; } = new(MockBehavior.Strict);
        public Mock<IUploadIdempotencyStore> Checkpoints { get; } = new(MockBehavior.Strict);
        public RecordingScanner Scanner { get; } = new();
        public WireObserver Wire { get; } = new();
        public Dictionary<string, (long Generation, byte[] Bytes)> Objects { get; } = [];
        public List<(string Name, string ContentType, byte[] Bytes)> Uploads { get; } = [];
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
            var claims = new List<Claim> { new("sub", "consumer-write-fixture") };
            if (identity != "denied")
                foreach (var permission in new[] { FilePermissions.Create, FilePermissions.Delete, FilePermissions.Read })
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
                        if (fault == "sign") throw new IOException("synthetic signer failure");
                        return Task.FromResult("AQ==");
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
                .Returns(new InvocationFunc(invocation => UploadAsync((StorageObject)invocation.Arguments[0], (Stream)invocation.Arguments[1],
                    (UploadObjectOptions)invocation.Arguments[2], (CancellationToken)invocation.Arguments[3])));
            Sdk.Setup(value => value.GetObjectAsync("maliev.com", It.IsAny<string>(), It.IsAny<GetObjectOptions>(), It.IsAny<CancellationToken>()))
                .Returns<string, string, GetObjectOptions, CancellationToken>((_, name, _, _) =>
                {
                    GetCalls++;
                    return Objects.TryGetValue(name, out var item)
                        ? Task.FromResult(new StorageObject { Generation = item.Generation, Size = (ulong)item.Bytes.Length })
                        : Task.FromException<StorageObject>(ApiError(HttpStatusCode.NotFound));
                });
            Sdk.Setup(value => value.CopyObjectAsync("maliev.com", It.IsAny<string>(), "maliev.com", It.IsAny<string>(), It.IsAny<CopyObjectOptions>(), It.IsAny<CancellationToken>()))
                .Returns<string, string, string, string, CopyObjectOptions, CancellationToken>((_, source, _, target, options, _) =>
                {
                    CopyCalls++;
                    Assert.Equal(17, options.SourceGeneration);
                    Assert.Equal(17, options.IfSourceGenerationMatch);
                    Assert.Equal(0, options.IfGenerationMatch);
                    Assert.False(Objects.ContainsKey(target));
                    Objects.Add(target, (31, Objects[source].Bytes));
                    return Task.FromResult(new StorageObject { Generation = 31 });
                });
            Sdk.Setup(value => value.DeleteObjectAsync("maliev.com", It.IsAny<string>(), It.IsAny<DeleteObjectOptions>(), It.IsAny<CancellationToken>()))
                .Returns<string, string, DeleteObjectOptions, CancellationToken>(async (_, name, options, token) =>
                {
                    Deletes.Add((name, options?.IfGenerationMatch));
                    if (fault == "wait-delete") await WaitForAbortAsync(token);
                    if (fault == "forbidden-delete") throw ApiError(HttpStatusCode.Forbidden);
                    if (!Objects.TryGetValue(name, out var item)) throw ApiError(HttpStatusCode.NotFound);
                    if (options?.IfGenerationMatch is long expected && expected != item.Generation) throw ApiError(HttpStatusCode.PreconditionFailed);
                    Objects.Remove(name);
                });
        }

        private async Task<StorageObject> UploadAsync(StorageObject item, Stream content, UploadObjectOptions options, CancellationToken token)
        {
            UploadCalls++;
            Assert.Equal("maliev.com", item.Bucket);
            Assert.Equal(0, options.IfGenerationMatch);
            if (fault == "wait-upload") await WaitForAbortAsync(token);
            using var bytes = new MemoryStream();
            await content.CopyToAsync(bytes, token);
            Uploads.Add((item.Name, item.ContentType, bytes.ToArray()));
            Objects.Add(item.Name, (17, bytes.ToArray()));
            if (fault == "unknown-upload") throw new IOException("synthetic upload acknowledgment lost");
            return new StorageObject { Generation = 17 };
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
public sealed class LegacyConsumerWritePostgreSqlCollection : ICollectionFixture<PostgreSqlFixture>
{
    public const string Name = "LegacyConsumerWritePostgreSQL";
}
