using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Google;
using Legacy.Maliev.Intranet.PurchaseOrders;
using Google.Cloud.Storage.V1;
using Legacy.Maliev.FileService.Api.Authorization;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Services;
using Legacy.Maliev.FileService.Data;
using Legacy.Maliev.FileService.Domain;
using Maliev.Aspire.ServiceDefaults.IAM;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Moq;
using StorageObject = Google.Apis.Storage.v1.Data.Object;

namespace Legacy.Maliev.FileService.Tests.Integration;

// Actual Program/auth/controller/application/PostgreSQL/GCS adapter and SDK signing template.
// Only GCS SDK/blob-signing effects are controlled; never live cloud or IAM evidence.
[Collection(LegacySignedReadPostgreSqlCollection.Name)]
public sealed class LegacySignedReadHttpBoundaryTests(PostgreSqlFixture fixture)
{
    [Theory]
    [InlineData("bucket", "same")]
    [InlineData("bucket", "different")]
    [InlineData("Bucket", "same")]
    [InlineData("BUCKET", "empty")]
    [InlineData("objectName", "same")]
    [InlineData("objectName", "different")]
    [InlineData("ObjectName", "same")]
    [InlineData("OBJECTNAME", "empty")]
    public async Task RepeatedObjectCoordinate_RejectsWithoutSigningOrChangingAuthority(string field, string variant)
    {
        await using var context = await ContextAsync();
        var name = Name();
        await SeedAsync(context, name);
        var beforeUploads = await context.Uploads.AsNoTracking().Where(row => row.Bucket == "private" && row.Name == name).ToArrayAsync();
        var beforeJournal = await context.StorageMoveJournals.AsNoTracking().Where(row => row.DestinationObjectName == name).ToArrayAsync();
        var value = variant == "empty" ? "" : field.Equals("bucket", StringComparison.OrdinalIgnoreCase)
            ? variant == "same" ? "private" : "another-bucket"
            : variant == "same" ? name : "orders/another-object.step";
        await using var factory = new SignedReadFactory(context.Database.GetConnectionString()!, name);
        using var client = factory.Client();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var response = await client.GetAsync(Query("/uploads/SignedUrl", name) + "&" + field + "=" + Uri.EscapeDataString(value), deadline.Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, factory.GetCalls);
        Assert.Equal(0, factory.SignCalls);
        Assert.Null(factory.SigningPayload);
        var afterUploads = await context.Uploads.AsNoTracking().Where(row => row.Bucket == "private" && row.Name == name).ToArrayAsync();
        var afterJournal = await context.StorageMoveJournals.AsNoTracking().Where(row => row.DestinationObjectName == name).ToArrayAsync();
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(beforeUploads), System.Text.Json.JsonSerializer.Serialize(afterUploads));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(beforeJournal), System.Text.Json.JsonSerializer.Serialize(afterJournal));
    }

    [Theory]
    [InlineData("anonymous", "bucket", HttpStatusCode.Unauthorized)]
    [InlineData("anonymous", "objectName", HttpStatusCode.Unauthorized)]
    [InlineData("wrong-key", "bucket", HttpStatusCode.Unauthorized)]
    [InlineData("wrong-key", "objectName", HttpStatusCode.Unauthorized)]
    [InlineData("wrong-permission", "bucket", HttpStatusCode.Forbidden)]
    [InlineData("wrong-permission", "objectName", HttpStatusCode.Forbidden)]
    public async Task RepeatedObjectCoordinate_RetainsAuthenticationAndPermissionPrecedence(string identity, string field, HttpStatusCode expected)
    {
        await using var context = await ContextAsync();
        await using var factory = new SignedReadFactory(context.Database.GetConnectionString()!, Name());
        using var client = factory.Client(identity);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var response = await client.GetAsync("/uploads/SignedUrl?bucket=private&objectName=orders%2Fpart.step&" + field + "=other", deadline.Token);

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(0, factory.GetCalls);
        Assert.Equal(0, factory.SignCalls);
    }

    [Theory]
    [InlineData("/uploads/SignedUrl")]
    [InlineData("/uploads/signedurl/")]
    public async Task ConfirmedRecord_UsesActualReadAdmissionAndJsonUriContract(string route)
    {
        await using var context = await ContextAsync();
        var name = Name();
        await SeedAsync(context, name);
        await using var factory = new SignedReadFactory(context.Database.GetConnectionString()!, name);
        using var client = factory.Client();
        using var response = await client.GetAsync(Query(route, name));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var uri = await response.Content.ReadFromJsonAsync<Uri>();
        Assert.NotNull(uri);
        Assert.Equal("https", uri.Scheme);
        Assert.Equal("storage.googleapis.com", uri.Host);
        Assert.Contains("X-Goog-Expires=604800", uri.Query, StringComparison.Ordinal);
        Assert.Contains("response-content-disposition=", uri.Query, StringComparison.Ordinal);
        Assert.Equal(1, factory.SignCalls);
        Assert.Equal(1, factory.GetCalls);
        Assert.Contains("generation=31", uri.Query, StringComparison.Ordinal);
        AssertSignedCanonicalDigest(factory, uri);
        Assert.Null(factory.Services.GetService<IIamServiceClient>());
        using var scope = factory.Services.CreateScope();
        Assert.IsType<FileApplicationService>(scope.ServiceProvider.GetRequiredService<IFileService>());
        Assert.IsType<GoogleCloudObjectStorage>(scope.ServiceProvider.GetRequiredService<IObjectStorage>());
        Assert.IsType<UploadRepository>(scope.ServiceProvider.GetRequiredService<IUploadRepository>());
        Assert.IsType<StorageMoveJournalRepository>(scope.ServiceProvider.GetRequiredService<IStorageReadJournal>());
        Assert.IsType<DisabledStorageMoveJournal>(scope.ServiceProvider.GetRequiredService<IStorageMoveJournal>());
    }

    [Theory]
    [InlineData("literal-only", true, false)]
    [InlineData("literal-and-decoy", true, true)]
    [InlineData("missing-literal-with-decoy", false, true)]
    public async Task PaddedObjectIdentity_SelectsOnlyRequestedMetadataJournalAndGeneration(
        string scenario, bool literalExists, bool decoyExists)
    {
        await using var context = await ContextAsync();
        var normalized = Name();
        var literal = "  " + normalized + "  ";
        if (literalExists) await SeedAsync(context, literal);
        if (decoyExists) await SeedAsync(context, normalized, generation: 47);
        var before = await context.StorageMoveJournals.AsNoTracking()
            .Where(row => row.DestinationObjectName == literal || row.DestinationObjectName == normalized)
            .OrderBy(row => row.OperationId).ToArrayAsync();
        var uploadsBefore = await context.Uploads.AsNoTracking()
            .Where(row => row.Bucket == "private" && (row.Name == literal || row.Name == normalized))
            .OrderBy(row => row.Id).ToArrayAsync();
        await using var factory = new SignedReadFactory(context.Database.GetConnectionString()!, literal)
        {
            AdditionalLiveObjects = decoyExists
                ? new Dictionary<string, long> { [normalized] = 47 }
                : new Dictionary<string, long>(),
        };
        using var client = factory.Client();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var response = await client.GetAsync(Query("/uploads/SignedUrl", literal), deadline.Token);

        var expectedStatus = literalExists ? HttpStatusCode.OK : HttpStatusCode.NotFound;
        Assert.True(response.StatusCode == expectedStatus,
            $"{scenario}: expected {expectedStatus}, observed {response.StatusCode}.");
        Assert.Equal(literalExists ? 1 : 0, factory.GetCalls);
        Assert.Equal(literalExists ? 1 : 0, factory.SignCalls);
        Assert.DoesNotContain(normalized, factory.GetObjectNames);
        if (literalExists)
        {
            Assert.Equal(literal, Assert.Single(factory.GetObjectNames));
            var uri = await response.Content.ReadFromJsonAsync<Uri>(deadline.Token);
            Assert.NotNull(uri);
            Assert.Equal("/private/" + literal, Uri.UnescapeDataString(uri.AbsolutePath));
            Assert.Contains("generation=31", uri.Query, StringComparison.Ordinal);
            Assert.DoesNotContain("generation=47", uri.Query, StringComparison.Ordinal);
            AssertSignedCanonicalDigest(factory, uri);
        }
        else
        {
            Assert.Empty(factory.GetObjectNames);
        }
        var after = await context.StorageMoveJournals.AsNoTracking()
            .Where(row => row.DestinationObjectName == literal || row.DestinationObjectName == normalized)
            .OrderBy(row => row.OperationId).ToArrayAsync();
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(before), System.Text.Json.JsonSerializer.Serialize(after));
        var uploadsAfter = await context.Uploads.AsNoTracking()
            .Where(row => row.Bucket == "private" && (row.Name == literal || row.Name == normalized))
            .OrderBy(row => row.Id).ToArrayAsync();
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(uploadsBefore), System.Text.Json.JsonSerializer.Serialize(uploadsAfter));
        Assert.Equal(literalExists, await context.Uploads.AnyAsync(row => row.Bucket == "private" && row.Name == literal));
        Assert.Equal(decoyExists, await context.Uploads.AnyAsync(row => row.Bucket == "private" && row.Name == normalized));
    }

    [Theory]
    [InlineData("\r")]
    [InlineData("\n")]
    [InlineData("\t")]
    [InlineData("\u0085")]
    public async Task LiteralRead_ControlPrefixNeverSelectsSafeNormalizedDecoy(string prefix)
    {
        await using var context = await ContextAsync();
        var decoy = Name();
        await SeedAsync(context, decoy);
        await using var factory = new SignedReadFactory(context.Database.GetConnectionString()!, decoy);
        using var client = factory.Client();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var response = await client.GetAsync(Query("/uploads/SignedUrl", prefix + decoy), deadline.Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, factory.GetCalls);
        Assert.Equal(0, factory.SignCalls);
        Assert.Empty(factory.GetObjectNames);
    }

    [Theory]
    [InlineData("padding")]
    [InlineData("slash-alias")]
    public async Task LiteralRead_QuarantineSafetyNamespaceStillBlocksAliases(string alias)
    {
        await using var context = await ContextAsync();
        var suffix = Name();
        var literal = alias == "padding" ? "  _quarantine/" + suffix + "  " : "///_quarantine//" + suffix;
        await SeedAsync(context, literal);
        await using var factory = new SignedReadFactory(context.Database.GetConnectionString()!, literal);
        using var client = factory.Client();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var response = await client.GetAsync(Query("/uploads/SignedUrl", literal), deadline.Token);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, factory.GetCalls);
        Assert.Equal(0, factory.SignCalls);
        Assert.Empty(factory.GetObjectNames);
    }

    [Fact]
    public async Task MissingMetadata_Returns404WithoutSigningOrCloudLookup()
    {
        await using var context = await ContextAsync();
        var name = Name();
        await using var factory = new SignedReadFactory(context.Database.GetConnectionString()!, name);
        using var client = factory.Client();
        using var response = await client.GetAsync(Query("/uploads/SignedUrl", name));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(factory.Services.GetService<IIamServiceClient>());
        Assert.Equal(0, factory.SignCalls);
        Assert.Equal(0, factory.GetCalls);
    }

    [Theory]
    [InlineData("anonymous", 401)]
    [InlineData("wrong-key", 401)]
    [InlineData("no-permission", 403)]
    [InlineData("wrong-permission", 403)]
    public async Task RealAdmissionFailure_HasNoStorageOrSigningEffect(string identity, int status)
    {
        await using var context = await ContextAsync();
        var name = Name();
        await SeedAsync(context, name);
        await using var factory = new SignedReadFactory(context.Database.GetConnectionString()!, name);
        using var client = factory.Client(identity);
        using var response = await client.GetAsync(Query("/uploads/SignedUrl", name));

        Assert.Equal(status, (int)response.StatusCode);
        Assert.Null(factory.Services.GetService<IIamServiceClient>());
        Assert.Equal(0, factory.GetCalls);
        Assert.Equal(0, factory.SignCalls);
    }

    [Theory]
    [InlineData("name-only", 200, 1)]
    [InlineData("missing-live", 404, 1)]
    [InlineData("generation-replaced", 503, 1)]
    [InlineData("not-clean", 503, 0)]
    [InlineData("quarantine", 503, 0)]
    [InlineData("revoked-journal", 503, 0)]
    [InlineData("revoked-absent", 503, 0)]
    [InlineData("incomplete", 503, 0)]
    [InlineData("ambiguous", 503, 0)]
    [InlineData("unknown", 503, 0)]
    [InlineData("invalid-generation", 503, 0)]
    [InlineData("invalid-source", 503, 0)]
    public async Task IntentionalGenerationAdaptation_DistinguishesHistoricalAbsenceAndUnsafeJournal(string state, int status, int getCalls)
    {
        await using var context = await ContextAsync();
        var name = (state == "quarantine" ? "_quarantine/" : "") + Name();
        await SeedAsync(context, name, state);
        await using var factory = new SignedReadFactory(context.Database.GetConnectionString()!, name)
        { LiveMissing = state == "missing-live", LiveGeneration = state == "generation-replaced" ? 47 : 31 };
        using var client = factory.Client();
        using var response = await client.GetAsync(Query("/uploads/SignedUrl", name));

        // PR59 recorded prior name-based behavior. These are explicitly reviewed new-generation oracles.
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(getCalls, factory.GetCalls);
        Assert.Equal(status == 200 ? 1 : 0, factory.SignCalls);
        Assert.Null(factory.Services.GetService<IIamServiceClient>());
        if (status == 200)
        {
            var uri = await response.Content.ReadFromJsonAsync<Uri>();
            Assert.NotNull(uri);
            Assert.Contains("generation=31", uri.Query, StringComparison.Ordinal);
            AssertSignedCanonicalDigest(factory, uri);
            // No journal row, migration flag or clean claim is manufactured for historical metadata.
            Assert.False(await context.StorageMoveJournals.AnyAsync(row => row.DestinationObjectName == name));
        }
        else
        {
            Assert.DoesNotContain(name, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task MetadataDeleted_AfterPriorSignedRead_NextRequestIs404WithoutCachedUrl()
    {
        await using var context = await ContextAsync();
        var name = Name();
        await SeedAsync(context, name);
        await using var factory = new SignedReadFactory(context.Database.GetConnectionString()!, name);
        using var client = factory.Client();
        using var first = await client.GetAsync(Query("/uploads/SignedUrl", name));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await context.Uploads.Where(row => row.Bucket == "private" && row.Name == name).ExecuteDeleteAsync();
        using var second = await client.GetAsync(Query("/uploads/SignedUrl", name));
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
        Assert.Equal(1, factory.SignCalls);
        Assert.Equal(1, factory.GetCalls);
        // Removing metadata prevents a new URL; previously issued cloud URLs are not thereby revoked.
    }

    [Fact]
    public async Task SigningDependencyFailure_RemainsOpaqueServerFailureNot404OrNullUri()
    {
        await using var context = await ContextAsync();
        var name = Name();
        await SeedAsync(context, name);
        await using var factory = new SignedReadFactory(context.Database.GetConnectionString()!, name) { FailSigning = true };
        using var client = factory.Client();
        using var response = await client.GetAsync(Query("/uploads/SignedUrl", name));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("private-signing-fixture", body, StringComparison.Ordinal);
        Assert.DoesNotContain(name, body, StringComparison.Ordinal);
        Assert.Equal(1, factory.SignCalls);
        Assert.Null(factory.Services.GetService<IIamServiceClient>());
    }

    [Theory]
    [InlineData("forbidden")]
    [InlineData("io")]
    [InlineData("invalid-object")]
    public async Task ProviderFailure_IsOpaque500NeverFalseAbsence(string failure)
    {
        await using var context = await ContextAsync();
        var name = Name();
        await SeedAsync(context, name);
        await using var factory = new SignedReadFactory(context.Database.GetConnectionString()!, name) { ReadFailure = failure };
        using var client = factory.Client();
        using var response = await client.GetAsync(Query("/uploads/SignedUrl", name));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(name, body, StringComparison.Ordinal);
        Assert.DoesNotContain("private-read-fixture", body, StringComparison.Ordinal);
        Assert.Equal(1, factory.GetCalls);
        Assert.Equal(0, factory.SignCalls);
    }

    [Fact]
    public async Task ReplacementAfterObservation_SignedCanonicalQueryStillSelectsObservedGeneration()
    {
        await using var context = await ContextAsync();
        var name = Name();
        await SeedAsync(context, name);
        await using var factory = new SignedReadFactory(context.Database.GetConnectionString()!, name) { ReplaceAtSigning = true };
        using var client = factory.Client();
        using var response = await client.GetAsync(Query("/uploads/SignedUrl", name));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var uri = await response.Content.ReadFromJsonAsync<Uri>();
        Assert.NotNull(uri);
        Assert.Equal(47, factory.LiveGeneration);
        Assert.Contains("generation=31", uri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("generation=47", uri.Query, StringComparison.Ordinal);
        AssertSignedCanonicalDigest(factory, uri);
        // Controlled SDK selection is not a claim about cloud retention or atomic revocation.
    }

    [Theory]
    [InlineData("name-only", 200)]
    [InlineData("missing-live", 404)]
    [InlineData("generation-replaced", 503)]
    [InlineData("sign-failure", 500)]
    [InlineData("provider-failure", 500)]
    public async Task ActualPinnedStrictConsumer_UsesRealFilePipelineAndPreservesFailureStatus(string state, int status)
    {
        await using var context = await ContextAsync();
        var name = Name();
        await SeedAsync(context, name, state);
        await using var factory = new SignedReadFactory(context.Database.GetConnectionString()!, name)
        {
            LiveMissing = state == "missing-live",
            LiveGeneration = state == "generation-replaced" ? 47 : 31,
            FailSigning = state == "sign-failure",
            ReadFailure = state == "provider-failure" ? "forbidden" : null,
        };
        using var client = factory.Client();
        var consumer = new LegacyFileClient(client);
        var token = client.DefaultRequestHeaders.Authorization!.Parameter!;
        if (status == 200)
        {
            var uri = await consumer.GetSignedUrlAsync("private", name, token, default);
            Assert.NotNull(uri);
            Assert.Contains("generation=31", uri.Query, StringComparison.Ordinal);
        }
        else if (status == 404)
        {
            Assert.Null(await consumer.GetSignedUrlAsync("private", name, token, default));
        }
        else
        {
            var failure = await Assert.ThrowsAsync<HttpRequestException>(() => consumer.GetSignedUrlAsync("private", name, token, default));
            Assert.Equal((HttpStatusCode)status, failure.StatusCode);
            Assert.DoesNotContain(name, failure.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ReadOnlyRuntime_StillRefusesUploadBeforeCloudWrite()
    {
        await using var context = await ContextAsync();
        var name = Name();
        await using var factory = new SignedReadFactory(context.Database.GetConnectionString()!, name);
        using var client = factory.Client("create");
        using var body = new MultipartFormDataContent();
        body.Add(new ByteArrayContent([1]), "files", "part.step");
        using var response = await client.PostAsync("/Uploads?bucket=private", body);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, factory.GetCalls);
        Assert.Equal(0, factory.SignCalls);
    }

    [Fact]
    public async Task ActualPinnedStrictConsumer_CancellationBeforeSendHasNoStorageEffect()
    {
        await using var context = await ContextAsync();
        var name = Name();
        await SeedAsync(context, name);
        await using var factory = new SignedReadFactory(context.Database.GetConnectionString()!, name);
        using var client = factory.Client();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LegacyFileClient(client).GetSignedUrlAsync(
            "private", name, client.DefaultRequestHeaders.Authorization!.Parameter!, canceled.Token));
        Assert.Equal(0, factory.GetCalls);
        Assert.Equal(0, factory.SignCalls);
    }

    [Fact]
    public async Task ActualPinnedStrictConsumer_MalformedJsonUriFailsInsteadOfReturningNull()
    {
        // Consumer-only wire failure; production's Uri serialization cannot emit this invalid JSON shape.
        using var client = new HttpClient(new MalformedUriHandler()) { BaseAddress = new Uri("https://file.example.invalid") };
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => new LegacyFileClient(client).GetSignedUrlAsync(
            "private", "orders/part.step", "controlled-token", default));
    }

    [Theory]
    [InlineData("name-only", StorageReadState.Absent)]
    [InlineData("clean", StorageReadState.Confirmed)]
    [InlineData("incomplete", StorageReadState.Incomplete)]
    [InlineData("not-clean", StorageReadState.Unclean)]
    [InlineData("revoked-journal", StorageReadState.Revoked)]
    [InlineData("ambiguous", StorageReadState.Ambiguous)]
    public async Task TypedJournal_ReadsRealPostgreSqlWithoutChangingAuthority(string state, StorageReadState expected)
    {
        await using var context = await ContextAsync();
        var name = Name();
        await SeedAsync(context, name, state);
        var before = await context.StorageMoveJournals.AsNoTracking().Where(row => row.DestinationObjectName == name)
            .OrderBy(row => row.OperationId).ToArrayAsync();
        var result = await new StorageMoveJournalRepository(context, TimeProvider.System).FindReadEvidenceAsync("private", name, default);
        Assert.Equal(expected, result.State);
        if (expected == StorageReadState.Confirmed) Assert.Equal(31, result.Evidence?.DestinationGeneration);
        else Assert.Null(result.Evidence);
        var after = await context.StorageMoveJournals.AsNoTracking().Where(row => row.DestinationObjectName == name)
            .OrderBy(row => row.OperationId).ToArrayAsync();
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(before), System.Text.Json.JsonSerializer.Serialize(after));
    }

    [Fact]
    public async Task TypedJournal_IncompatiblePhysicalSchemaThrowsInsteadOfInferringHistoricalAbsence()
    {
        await using var context = await ContextAsync();
        // Transactional DDL is isolated to this test's own collection database and always rolled back.
        await using var transaction = await context.Database.BeginTransactionAsync();
        try
        {
            await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"StorageMoveJournal\" RENAME COLUMN \"SourceGeneration\" TO \"UnexpectedSourceGeneration\"");
            await Assert.ThrowsAsync<UploadOutcomeUnknownException>(() => new StorageMoveJournalRepository(context, TimeProvider.System)
                .FindReadEvidenceAsync("private", Name(), default));
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    private sealed class MalformedUriHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"Uri\":false}", Encoding.UTF8, "application/json") });
    }

    private static void AssertSignedCanonicalDigest(SignedReadFactory factory, Uri uri)
    {
        var query = string.Join("&", uri.Query.TrimStart('?').Split('&')
            .Where(value => !value.StartsWith("X-Goog-Signature=", StringComparison.Ordinal)));
        var canonical = "GET\n" + uri.AbsolutePath + "\n" + query + "\nhost:storage.googleapis.com\n\nhost\nUNSIGNED-PAYLOAD";
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        Assert.NotNull(factory.SigningPayload);
        Assert.EndsWith("\n" + digest, Encoding.UTF8.GetString(factory.SigningPayload), StringComparison.Ordinal);
    }

    private async Task<FileDbContext> ContextAsync()
    {
        var context = fixture.CreateContext();
        await context.Database.MigrateAsync();
        return context;
    }

    private static string Name() => "orders/" + Guid.NewGuid().ToString("N") + "/ชิ้นงาน.step";
    private static string Query(string route, string name) => route + "?bucket=private&objectName=" + Uri.EscapeDataString(name);

    private static async Task SeedAsync(FileDbContext context, string name, string state = "clean", long generation = 31)
    {
        context.Uploads.Add(new Upload { Bucket = "private", Name = name, ContentType = "application/octet-stream", Size = 7 });
        if (state != "name-only") context.StorageMoveJournals.Add(new StorageMoveJournal
        {
            OperationId = Guid.NewGuid(),
            ScanClean = state != "not-clean",
            SourceBucket = "private",
            SourceObjectName = state == "invalid-source" ? "" : "_quarantine/" + name,
            SourceGeneration = 17,
            DestinationBucket = "private",
            DestinationObjectName = name,
            DestinationGeneration = state == "invalid-generation" ? null : generation,
            State = state switch
            {
                "revoked-journal" => "CompensatedRemoved",
                "revoked-absent" => "CompensatedAbsent",
                "incomplete" => "MetadataSubmitting",
                "unknown" => "CompensationUnknown",
                _ => "MetadataCommitted",
            },
            CreatedAt = DateTimeOffset.UtcNow,
            ModifiedAt = DateTimeOffset.UtcNow,
        });
        if (state == "ambiguous") context.StorageMoveJournals.Add(new StorageMoveJournal
        {
            OperationId = Guid.NewGuid(),
            ScanClean = true,
            SourceBucket = "private",
            SourceObjectName = "_quarantine/" + name,
            SourceGeneration = 18,
            DestinationBucket = "private",
            DestinationObjectName = name,
            DestinationGeneration = 31,
            State = "MetadataCommitted",
            CreatedAt = DateTimeOffset.UtcNow,
            ModifiedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync();
    }

    private sealed class SignedReadFactory(string connection, string name) : WebApplicationFactory<Program>
    {
        private readonly RSA key = RSA.Create(2048);
        private readonly string principal = "signed-read-" + Guid.NewGuid().ToString("N");
        public bool LiveMissing { get; init; }
        public long LiveGeneration { get; set; } = 31;
        public string? ReadFailure { get; init; }
        public bool ReplaceAtSigning { get; init; }
        public byte[]? SigningPayload { get; private set; }
        public bool FailSigning { get; init; }
        public int GetCalls { get; private set; }
        public int SignCalls { get; private set; }
        public List<string> GetObjectNames { get; } = [];
        public IReadOnlyDictionary<string, long> AdditionalLiveObjects { get; init; } = new Dictionary<string, long>();

        public HttpClient Client(string identity = "read")
        {
            var client = CreateClient();
            if (identity == "anonymous") return client;
            using var wrongKey = identity == "wrong-key" ? RSA.Create(2048) : null;
            var now = DateTime.UtcNow;
            // File's unchanged registration has no IAM client. Exercise its real signed-claim admission.
            var claims = new List<Claim> { new(JwtRegisteredClaimNames.Sub, principal) };
            if (identity is "read" or "wrong-key") claims.Add(new("permissions", FilePermissions.Read));
            if (identity is "wrong-permission" or "create") claims.Add(new("permissions", FilePermissions.Create));
            var token = new JwtSecurityToken("https://issuer.example.invalid", "https://file.example.invalid",
                claims, now.AddMinutes(-1), now.AddMinutes(5),
                new SigningCredentials(new RsaSecurityKey(wrongKey ?? key), SecurityAlgorithms.RsaSha256));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
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
            builder.UseSetting("FileStorage:WritesEnabled", "false");
            builder.UseSetting("FileStorage:AllowedBuckets:0", "private");
            builder.UseSetting("FileStorage:SignedUrlHours", "168");
            builder.ConfigureServices(services =>
            {
                var sdk = new Mock<StorageClient>(MockBehavior.Strict);
                sdk.Setup(value => value.GetObjectAsync("private", name, It.IsAny<GetObjectOptions>(), It.IsAny<CancellationToken>()))
                    .Returns(() =>
                    {
                        GetCalls++;
                        GetObjectNames.Add(name);
                        if (ReadFailure == "forbidden") return Task.FromException<StorageObject>(new GoogleApiException("storage", "private-read-fixture") { HttpStatusCode = HttpStatusCode.Forbidden });
                        if (ReadFailure == "io") return Task.FromException<StorageObject>(new IOException("private-read-fixture"));
                        if (ReadFailure == "invalid-object") return Task.FromResult(new StorageObject { Generation = 0, Size = 7 });
                        return LiveMissing ? Task.FromException<StorageObject>(new GoogleApiException("storage", "controlled absent object") { HttpStatusCode = HttpStatusCode.NotFound })
                            : Task.FromResult(new StorageObject { Bucket = "private", Name = name, Generation = LiveGeneration, Size = 7 });
                    });
                foreach (var alternative in AdditionalLiveObjects)
                {
                    sdk.Setup(value => value.GetObjectAsync("private", alternative.Key,
                            It.IsAny<GetObjectOptions>(), It.IsAny<CancellationToken>()))
                        .Returns(() =>
                        {
                            GetCalls++;
                            GetObjectNames.Add(alternative.Key);
                            return Task.FromResult(new StorageObject
                            {
                                Bucket = "private",
                                Name = alternative.Key,
                                Generation = alternative.Value,
                                Size = 7,
                            });
                        });
                }
                var signer = new Mock<UrlSigner.IBlobSigner>(MockBehavior.Strict);
                signer.SetupGet(value => value.Id).Returns("controlled@example.invalid");
                signer.SetupGet(value => value.Algorithm).Returns("GOOG4-RSA-SHA256");
                signer.Setup(value => value.CreateSignatureAsync(It.IsAny<byte[]>(), It.IsAny<UrlSigner.BlobSignerParameters>(), It.IsAny<CancellationToken>()))
                    .Returns((byte[] payload, UrlSigner.BlobSignerParameters parameters, CancellationToken token) =>
                    {
                        SigningPayload = payload.ToArray();
                        if (ReplaceAtSigning) LiveGeneration = 47;
                        SignCalls++;
                        return FailSigning ? Task.FromException<string>(new IOException("private-signing-fixture")) : Task.FromResult("AQ==");
                    });
                services.RemoveAll<StorageClient>();
                services.AddSingleton(sdk.Object);
                services.RemoveAll<UrlSigner>();
                services.AddSingleton(UrlSigner.FromBlobSigner(signer.Object));
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) key.Dispose();
        }
    }

}

[CollectionDefinition(Name)]
public sealed class LegacySignedReadPostgreSqlCollection : ICollectionFixture<PostgreSqlFixture>
{
    public const string Name = "LegacySignedReadPostgreSQL";
}
