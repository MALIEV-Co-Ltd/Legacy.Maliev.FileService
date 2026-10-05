using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Data.Common;
using DotNet.Testcontainers.Builders;
using Google;
using Google.Cloud.Storage.V1;
using Legacy.Maliev.FileService.Api.Authorization;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Models;
using Legacy.Maliev.FileService.Application.Services;
using Legacy.Maliev.FileService.Data;
using Legacy.Maliev.FileService.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Moq;
using Npgsql;
using StackExchange.Redis;
using StorageObject = Google.Apis.Storage.v1.Data.Object;

namespace Legacy.Maliev.FileService.Tests.Integration;

// Controlled GCS SDK/scan outcomes are component fault evidence, not live-provider or malware certification.
[Collection(PostgreSqlCollection.Name)]
public sealed class FileStorageRecoveryBoundaryTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task UploadRpc_BeforeAnyProviderEffect_HasDurablePrivateCoordinates()
    {
        await using var context = await ContextAsync();
        var cloud = new ControlledCloud { LoseUploadAcknowledgment = true };
        var observedAuthority = false;
        cloud.BeforeUpload = async item =>
        {
            await using var observer = fixture.CreateContext();
            observedAuthority = await IntentExistsAsync(observer, item.Bucket, item.Name);
        };

        await Record.ExceptionAsync(() => Service(context, cloud).UploadAsync("private", Prefix(), Files(), default));

        Assert.Equal(1, cloud.UploadCalls);
        Assert.True(observedAuthority, "No durable per-object authority existed before the initial upload RPC.");
    }

    [Fact]
    public async Task UploadRpc_LostAcknowledgment_RetainsDurableCoordinatesWithoutScanOrPromotion()
    {
        await using var context = await ContextAsync();
        var cloud = new ControlledCloud { LoseUploadAcknowledgment = true };
        var scanner = new ControlledScanner();

        await Record.ExceptionAsync(() => Service(context, cloud, scanner).UploadAsync("private", Prefix(), Files(), default));

        Assert.Single(cloud.Objects); // The controlled provider applied the private upload, then lost its response.
        Assert.Equal(0, scanner.Calls);
        Assert.Equal(0, cloud.CopyCalls);
        Assert.Equal(0, cloud.SignCalls);
        Assert.Empty(cloud.DeleteAttempts);
        var name = Assert.Single(cloud.Objects).Key;
        Assert.False(await context.Uploads.AnyAsync(row => row.Name == name));
        Assert.True(await IntentExistsAsync(context, "private", name),
            "A lost initial upload response has no durable quarantine recovery record.");
    }

    [Fact]
    public async Task InitialIntent_UnknownOperationReplay_CannotUploadAgainOrResetAuthority()
    {
        await using var context = await ContextAsync();
        var cloud = new ControlledCloud { LoseUploadAcknowledgment = true };
        var service = Service(context, cloud);
        var operation = Guid.NewGuid();
        var prefix = Prefix();
        await Assert.ThrowsAsync<UploadOutcomeUnknownException>(() => service.UploadAsync("private", prefix, Files(), operation, default));
        var before = await context.QuarantineUploadIntents.AsNoTracking().SingleAsync(row => row.ParentOperationId == operation);

        await Assert.ThrowsAsync<UploadOutcomeUnknownException>(() => service.UploadAsync("private", prefix, Files(), operation, default));

        Assert.Equal(1, cloud.UploadCalls);
        Assert.Equal(0, cloud.CopyCalls);
        Assert.Equal(0, cloud.SignCalls);
        var after = await context.QuarantineUploadIntents.AsNoTracking().SingleAsync(row => row.ParentOperationId == operation);
        Assert.Equal("Unknown", after.State);
        Assert.Null(after.AcknowledgedGeneration);
        Assert.Equal(before.OperationId, after.OperationId);
        Assert.Equal(before.ObjectName, after.ObjectName);
        Assert.Equal(before.CreatedAt, after.CreatedAt);
        Assert.Equal(before.ModifiedAt, after.ModifiedAt);
    }

    [Fact]
    public async Task InitialIntent_AcknowledgmentCommitThenResponseLost_RetainsPositiveGenerationWithoutScan()
    {
        await using var context = await ContextAsync();
        var cloud = new ControlledCloud();
        var scanner = new ControlledScanner();
        var intent = new FaultingIntent(new QuarantineUploadIntentRepository(context, TimeProvider.System)) { LoseAcknowledgment = true };
        var prefix = Prefix();

        var failure = await Assert.ThrowsAsync<UploadOutcomeUnknownException>(() =>
            Service(context, cloud, scanner, intents: intent).UploadAsync("private", prefix, Files(), default));

        Assert.Same(intent.Failure, failure.InnerException);
        Assert.Equal(1, cloud.UploadCalls);
        Assert.Equal(0, scanner.Calls);
        Assert.Equal(0, cloud.CopyCalls);
        Assert.Equal(0, cloud.SignCalls);
        Assert.Empty(cloud.DeleteAttempts);
        var name = Assert.Single(cloud.Objects).Key;
        var row = await context.QuarantineUploadIntents.AsNoTracking().SingleAsync(item => item.ObjectName == name);
        Assert.Equal("Unknown", row.State);
        Assert.Equal(17, row.AcknowledgedGeneration);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitialIntent_UnknownCheckpointFailsOrTimesOut_CanceledCallerCannotErasePendingAuthority(bool timeout)
    {
        await using var context = await ContextAsync();
        using var request = new CancellationTokenSource();
        var cloud = new ControlledCloud { LoseUploadAcknowledgment = true, CancelAtOutcome = request };
        var scanner = new ControlledScanner();
        var intent = new FaultingIntent(new QuarantineUploadIntentRepository(context, TimeProvider.System))
        { FailUnknown = !timeout, TimeoutUnknown = timeout };

        await Assert.ThrowsAsync<UploadOutcomeUnknownException>(() =>
            Service(context, cloud, scanner, intents: intent).UploadAsync("private", Prefix(), Files(), request.Token));

        Assert.True(request.IsCancellationRequested);
        Assert.True(intent.UnknownToken.CanBeCanceled);
        Assert.NotEqual(request.Token, intent.UnknownToken);
        if (timeout) Assert.True(intent.UnknownToken.IsCancellationRequested);
        Assert.Equal(0, scanner.Calls);
        Assert.Equal(0, cloud.CopyCalls);
        Assert.Equal(0, cloud.SignCalls);
        Assert.Empty(cloud.DeleteAttempts);
        var name = Assert.Single(cloud.Objects).Key;
        var row = await context.QuarantineUploadIntents.AsNoTracking().SingleAsync(item => item.ObjectName == name);
        Assert.Equal("Pending", row.State);
        Assert.Null(row.AcknowledgedGeneration);
    }

    [Theory]
    [InlineData("source-bucket")]
    [InlineData("source-name")]
    [InlineData("source-generation")]
    [InlineData("destination-bucket")]
    [InlineData("destination-name")]
    [InlineData("scan-verdict")]
    public async Task JournalBegin_OperationIdBoundToDifferentTuple_ReturnsFalseAndPreservesWinner(string mutation)
    {
        await using var context = await ContextAsync();
        var journal = new StorageMoveJournalRepository(context, TimeProvider.System);
        var id = Guid.NewGuid();
        Assert.True(await journal.BeginAsync(id, true, "private", "quarantine/a", 17, "private", "clean/a", default));
        await journal.CopiedAsync(id, 31, default);
        var before = await context.StorageMoveJournals.AsNoTracking().SingleAsync(row => row.OperationId == id);

        Assert.False(await journal.BeginAsync(id,
            mutation != "scan-verdict", mutation == "source-bucket" ? "other" : "private",
            mutation == "source-name" ? "quarantine/b" : "quarantine/a", mutation == "source-generation" ? 19 : 17,
            mutation == "destination-bucket" ? "other" : "private",
            mutation == "destination-name" ? "clean/b" : "clean/a", default));

        var after = await context.StorageMoveJournals.AsNoTracking().SingleAsync(row => row.OperationId == id);
        Assert.True(after.ScanClean);
        Assert.Equal("private", after.SourceBucket);
        Assert.Equal("quarantine/a", after.SourceObjectName);
        Assert.Equal(17, after.SourceGeneration);
        Assert.Equal("private", after.DestinationBucket);
        Assert.Equal("clean/a", after.DestinationObjectName);
        Assert.Equal(31, after.DestinationGeneration);
        Assert.Equal("Copied", after.State);
        Assert.Equal(before.CreatedAt, after.CreatedAt);
        Assert.Equal(before.ModifiedAt, after.ModifiedAt);
    }

    [Fact]
    public async Task JournalBegin_IdenticalReplay_DoesNotResetCopiedEvidenceOrDates()
    {
        await using var context = await ContextAsync();
        var journal = new StorageMoveJournalRepository(context, TimeProvider.System);
        var id = Guid.NewGuid();
        Assert.True(await journal.BeginAsync(id, true, "private", "quarantine/a", 17, "private", "clean/a", default));
        await journal.CopiedAsync(id, 31, default);
        var before = await context.StorageMoveJournals.AsNoTracking().SingleAsync(row => row.OperationId == id);

        Assert.False(await journal.BeginAsync(id, true, "private", "quarantine/a", 17, "private", "clean/a", default));

        var after = await context.StorageMoveJournals.AsNoTracking().SingleAsync(row => row.OperationId == id);
        Assert.Equal("Copied", after.State);
        Assert.Equal(31, after.DestinationGeneration);
        Assert.Equal(before.CreatedAt, after.CreatedAt);
        Assert.Equal(before.ModifiedAt, after.ModifiedAt);
    }

    [Fact]
    public async Task JournalMigration_DownCannotDestroyUnresolvedRecoveryAuthority()
    {
        await using var context = await ContextAsync();
        var journal = new StorageMoveJournalRepository(context, TimeProvider.System);
        var id = Guid.NewGuid();
        Assert.True(await journal.BeginAsync(id, true, "private", "quarantine/a", 17, "private", "clean/a", default));
        await journal.CopiedAsync(id, 31, default);
        await journal.UnknownAsync(id, default);
        var assembly = context.GetService<IMigrationsAssembly>();
        var migration = assembly.CreateMigration(assembly.Migrations["20260929015203_AddStorageMoveJournal"], context.Database.ProviderName!);
        Assert.Throws<NotSupportedException>(() => migration.DownOperations);
        Assert.NotNull(await journal.FindAsync(id, default));
    }

    [Fact]
    public async Task RecoveryMigrations_RealDowngradeRefused_RetainsRowsSchemaAndMigrationHistory()
    {
        await using var context = await ContextAsync();
        var intent = new QuarantineUploadIntentRepository(context, TimeProvider.System);
        var id = Guid.NewGuid();
        await intent.PrepareAsync(id, Guid.NewGuid(), "private", "quarantine/retained-" + id.ToString("N"), "model/stl", 1, default);
        await intent.UnknownAsync(id, default);
        var journal = new StorageMoveJournalRepository(context, TimeProvider.System);
        Assert.True(await journal.BeginAsync(id, true, "private", "quarantine/retained", 17, "private", "clean/retained", default));
        await journal.CopiedAsync(id, 31, default);
        await journal.UnknownAsync(id, default);
        var before = (await context.Database.GetAppliedMigrationsAsync()).ToArray();

        await Assert.ThrowsAsync<NotSupportedException>(() => context.GetService<IMigrator>().MigrateAsync("20260719033405_AddInstantQuoteUploadWorkflow"));

        Assert.Equal(before, await context.Database.GetAppliedMigrationsAsync());
        var row = await context.QuarantineUploadIntents.AsNoTracking().SingleAsync(item => item.OperationId == id);
        Assert.Equal("Unknown", row.State);
        Assert.Null(row.AcknowledgedGeneration);
        var move = await journal.FindAsync(id, default);
        Assert.NotNull(move);
        Assert.Equal("Unknown", move.State);
        Assert.Equal(31, move.DestinationGeneration);
        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Signing_DefinitePreMetadataFailure_CompensatesOnlyConfirmedDestinationGeneration(bool cancel, bool replaced)
    {
        await using var context = await ContextAsync();
        using var request = new CancellationTokenSource();
        var cloud = new ControlledCloud { FailSigning = true, ReplaceBeforeSigning = replaced, CancelAtSigning = cancel ? request : null };
        var prefix = Prefix();

        await Record.ExceptionAsync(() => Service(context, cloud).UploadAsync("private", prefix, Files(), request.Token));

        Assert.Equal(1, cloud.SignCalls);
        var move = await context.StorageMoveJournals.AsNoTracking().SingleAsync(row => row.DestinationObjectName.StartsWith(prefix));
        Assert.Equal(31, move.DestinationGeneration);
        Assert.False(cloud.Objects.ContainsKey(move.SourceObjectName));
        Assert.False(await context.Uploads.AnyAsync(row => row.Name.StartsWith(prefix)));
        var attempted = Assert.Single(cloud.DeleteAttempts, item => item.Generation == 31);
        Assert.False(attempted.Canceled, "Compensation inherited caller cancellation.");
        Assert.DoesNotContain(cloud.DeleteAttempts, item => item.Generation is null);
        if (replaced) Assert.Equal(47, Assert.Single(cloud.Objects).Value);
        else Assert.Empty(cloud.Objects);
    }

    [Fact]
    public async Task Signing_DefiniteFailureAndConditionalCleanupFault_RetainsBothCausesAndCoordinates()
    {
        await using var context = await ContextAsync();
        var cloud = new ControlledCloud { FailSigning = true, FailDestinationCleanup = true };

        var failure = await Assert.ThrowsAsync<UploadRollbackException>(() =>
            Service(context, cloud).UploadAsync("private", Prefix(), Files(), default));

        Assert.Same(cloud.SigningFailure, failure.UploadFailure);
        Assert.Equal(new Exception[] { cloud.SigningFailure, cloud.CleanupFailure },
            Assert.IsType<AggregateException>(failure.InnerException).InnerExceptions);
        Assert.Single(failure.CleanupFailures);
        Assert.Same(cloud.CleanupFailure, failure.CleanupFailures[0].Cause);
        var destination = Assert.Single(cloud.Objects);
        var row = await context.StorageMoveJournals.AsNoTracking().SingleAsync(item => item.DestinationObjectName == destination.Key);
        Assert.Equal(31, row.DestinationGeneration);
        Assert.NotEqual("MetadataCommitted", row.State);
    }

    [Theory]
    [InlineData("copy", false)]
    [InlineData("delete", false)]
    [InlineData("copy", true)]
    [InlineData("delete", true)]
    public async Task Move_UnknownProviderOutcome_PreservesCopiesAndNeverSignsOrCommitsMetadata(string stage, bool canceled)
    {
        await using var context = await ContextAsync();
        using var request = new CancellationTokenSource();
        var cloud = new ControlledCloud
        {
            LoseCopyAcknowledgment = stage == "copy",
            LoseDeleteAcknowledgment = stage == "delete",
            CancelAtOutcome = canceled ? request : null,
        };
        var prefix = Prefix();

        await Assert.ThrowsAsync<UploadOutcomeUnknownException>(() => Service(context, cloud).UploadAsync("private", prefix, Files(), request.Token));

        Assert.Equal(0, cloud.SignCalls);
        Assert.False(await context.Uploads.AnyAsync(row => row.Name.StartsWith(prefix)));
        Assert.Contains(cloud.Objects, item => item.Key.StartsWith(prefix) && item.Value == 31);
        Assert.DoesNotContain(cloud.DeleteAttempts, item => item.Generation == 31 || item.Generation is null);
        var row = await context.StorageMoveJournals.AsNoTracking().SingleAsync(item => item.DestinationObjectName.StartsWith(prefix));
        Assert.Equal("Unknown", row.State);
        Assert.Equal(17, row.SourceGeneration);
        if (stage == "copy") Assert.Null(row.DestinationGeneration);
        else Assert.Equal(31, row.DestinationGeneration);
    }

    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("wrong-key", HttpStatusCode.Unauthorized)]
    [InlineData("no-grant", HttpStatusCode.Forbidden)]
    [InlineData("create", HttpStatusCode.ServiceUnavailable)]
    public async Task ProductionHttp_PostUsesRealRs256AndPermissionPipeline(string identity, HttpStatusCode expected)
    {
        await using var context = await ContextAsync();
        var cloud = new ControlledCloud { LoseCopyAcknowledgment = true };
        using var factory = new RecoveryFactory(context.Database.GetConnectionString()!, cloud);
        using var client = factory.CreateClient();
        using var body = Multipart();
        if (identity != "anonymous") client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Token(identity));

        using var response = await client.PostAsync($"/Uploads?bucket=private&path={Prefix()}", body);

        Assert.True(expected == response.StatusCode, $"Expected {expected}; actual {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        if (identity == "create")
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("quarantine", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("part.stl", text, StringComparison.Ordinal);
            Assert.Equal(1, cloud.CopyCalls);
            Assert.Equal(0, cloud.SignCalls);
        }
        else Assert.Equal(0, cloud.UploadCalls);
    }

    [Fact]
    public async Task ProductionHttp_PutNameOnlyMetadata_CannotCertifyAnUnscannedGeneration()
    {
        await using var context = await ContextAsync();
        var source = Prefix() + "/historical.stl";
        var destination = Prefix() + "/moved.stl";
        context.Uploads.Add(new Upload { Bucket = "private", Name = source, ContentType = "model/stl", Size = 1 });
        await context.SaveChangesAsync();
        var cloud = new ControlledCloud();
        cloud.Objects[source] = 17; // Adversarial provider object: no complete-scan evidence exists for this generation.
        using var factory = new RecoveryFactory(context.Database.GetConnectionString()!, cloud);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Token("update"));

        using var response = await client.PutAsync($"/Uploads?sourceBucket=private&sourceObjectName={source}&destinationBucket=private&destinationObjectName={destination}", null);

        Assert.True(response.StatusCode == HttpStatusCode.ServiceUnavailable, $"Actual {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        Assert.Equal(0, cloud.CopyCalls);
        Assert.True(await context.Uploads.AsNoTracking().AnyAsync(row => row.Name == source));
        Assert.False(await context.StorageMoveJournals.AnyAsync(row => row.SourceObjectName == source && row.ScanClean));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProductionHttp_PutCommittedCleanGeneration_RequiresExactLiveMatch(bool drifted)
    {
        await using var context = await ContextAsync();
        var source = Prefix() + "/proven.stl";
        var destination = Prefix() + "/moved.stl";
        context.Uploads.Add(new Upload { Bucket = "private", Name = source, ContentType = "model/stl", Size = 1 });
        await context.SaveChangesAsync();
        var journal = new StorageMoveJournalRepository(context, TimeProvider.System);
        var evidenceId = Guid.NewGuid();
        Assert.True(await journal.BeginAsync(evidenceId, true, "private", "_quarantine/proven-" + evidenceId.ToString("N"), 17,
            "private", source, default));
        await journal.CopiedAsync(evidenceId, 31, default);
        await journal.SourceDeletedAsync(evidenceId, default);
        await journal.MetadataCommittedAsync(evidenceId, default);
        var generation = drifted ? 47 : 31;
        var cloud = new ControlledCloud { ExpectedCopySourceGeneration = generation };
        cloud.Objects[source] = generation;
        using var factory = new RecoveryFactory(context.Database.GetConnectionString()!, cloud);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Token("update"));

        using var response = await client.PutAsync($"/Uploads?sourceBucket=private&sourceObjectName={source}&destinationBucket=private&destinationObjectName={destination}", null);

        if (drifted)
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal(0, cloud.CopyCalls);
            Assert.Empty(cloud.DeleteAttempts);
            Assert.Equal(47, cloud.Objects[source]);
            Assert.True(await context.Uploads.AsNoTracking().AnyAsync(row => row.Name == source));
            Assert.Equal(1, await context.StorageMoveJournals.CountAsync(row => row.DestinationObjectName == source || row.DestinationObjectName == destination));
        }
        else
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Equal(new long[] { 31 }, cloud.CopySourceGenerations);
            Assert.False(cloud.Objects.ContainsKey(source));
            Assert.True(await context.Uploads.AsNoTracking().AnyAsync(row => row.Name == destination));
            var move = await context.StorageMoveJournals.AsNoTracking().SingleAsync(row => row.DestinationObjectName == destination);
            Assert.True(move.ScanClean);
            Assert.Equal(31, move.SourceGeneration);
            Assert.Equal("MetadataCommitted", move.State);
        }
    }

    [Theory]
    [InlineData("intent-column")]
    [InlineData("intent-constraint")]
    [InlineData("move-column")]
    public async Task InitialUpload_PartialPhysicalRecoverySchema_RefusesBeforeProviderEffect(string missing)
    {
        await using var context = await ContextAsync();
        await using var transaction = await context.Database.BeginTransactionAsync();
        try
        {
            var sql = missing switch
            {
                "intent-column" => "ALTER TABLE \"QuarantineUploadIntent\" DROP COLUMN \"ModifiedAt\"",
                "intent-constraint" => "ALTER TABLE \"QuarantineUploadIntent\" DROP CONSTRAINT \"CK_QuarantineUploadIntent_Generation\"",
                _ => "ALTER TABLE \"StorageMoveJournal\" DROP COLUMN \"DestinationGeneration\" CASCADE",
            };
            await context.Database.ExecuteSqlRawAsync(sql);
            var cloud = new ControlledCloud();

            await Assert.ThrowsAsync<UploadOutcomeUnknownException>(() => Service(context, cloud).UploadAsync("private", Prefix(), Files(), default));

            Assert.Equal(0, cloud.UploadCalls);
            Assert.Equal(0, cloud.CopyCalls);
            Assert.Equal(0, cloud.SignCalls);
            Assert.Empty(cloud.Objects);
        }
        finally { await transaction.RollbackAsync(); }
    }

    [Fact]
    public async Task ProductionHttp_InitialUploadResponseLost_ReturnsRedactedUnavailableNotRawServerFailure()
    {
        await using var context = await ContextAsync();
        var cloud = new ControlledCloud { LoseUploadAcknowledgment = true };
        using var factory = new RecoveryFactory(context.Database.GetConnectionString()!, cloud);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Token("create"));
        using var body = Multipart();

        using var response = await client.PostAsync($"/Uploads?bucket=private&path={Prefix()}", body);

        Assert.Equal(1, cloud.UploadCalls);
        Assert.Equal(0, cloud.CopyCalls);
        Assert.Equal(0, cloud.SignCalls);
        Assert.Empty(cloud.DeleteAttempts);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("controlled upload acknowledgment", text, StringComparison.Ordinal);
        Assert.DoesNotContain("part.stl", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProductionHttp_MissingPhysicalJournal_RefusesBeforeFirstCloudMutation()
    {
        await using var master = fixture.CreateContext();
        var connection = new NpgsqlConnectionStringBuilder(master.Database.GetConnectionString());
        var database = "recovery_" + Guid.NewGuid().ToString("N");
        await using var administrative = new NpgsqlConnection(connection.ConnectionString);
        await administrative.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", administrative)) await create.ExecuteNonQueryAsync();
        try
        {
            connection.Database = database;
            await using var context = new FileDbContext(new DbContextOptionsBuilder<FileDbContext>().UseNpgsql(connection.ConnectionString).Options);
            await context.Database.MigrateAsync("20260719033405_AddInstantQuoteUploadWorkflow");
            var cloud = new ControlledCloud();
            using var factory = new RecoveryFactory(connection.ConnectionString, cloud);
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Token("create"));
            using var body = Multipart();

            using var response = await client.PostAsync($"/Uploads?bucket=private&path={Prefix()}", body);

            Assert.Equal(0, cloud.UploadCalls);
            Assert.Equal(0, cloud.CopyCalls);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{database}\" WITH (FORCE)", administrative);
            await drop.ExecuteNonQueryAsync(); // Unique database exists only inside the owned Testcontainers instance.
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Metadata_RealCommitThenAcknowledgmentLost_PreservesEveryPromotionAndOriginalCause(bool canceled)
    {
        await using var context = await ContextAsync();
        using var request = new CancellationTokenSource();
        var cloud = new ControlledCloud { AllowControlledSigning = true };
        var repository = new CommitThenLoseAcknowledgment(new UploadRepository(context, TimeProvider.System), canceled ? request : null);
        var prefix = Prefix();

        var failure = await Assert.ThrowsAsync<UploadOutcomeUnknownException>(() => Service(context, cloud, repository: repository)
            .UploadAsync("private", prefix, [new ControlledFile("first.stl"), new ControlledFile("second.stl")], request.Token));

        Assert.Same(repository.Failure, failure.InnerException);
        Assert.Equal(2, await context.Uploads.AsNoTracking().CountAsync(row => row.Name.StartsWith(prefix)));
        Assert.Equal(2, cloud.SignCalls);
        Assert.Equal(2, cloud.Objects.Count);
        Assert.All(cloud.Objects, item => Assert.Equal(31, item.Value));
        Assert.DoesNotContain(cloud.DeleteAttempts, item => item.Generation == 31 || item.Generation is null);
        var rows = await context.StorageMoveJournals.AsNoTracking().Where(row => row.DestinationObjectName.StartsWith(prefix)).ToArrayAsync();
        Assert.Equal(2, rows.Length);
        Assert.All(rows, row => { Assert.Equal("MetadataSubmitting", row.State); Assert.Equal(31, row.DestinationGeneration); });
        Assert.False(await new StorageMoveJournalRepository(context, TimeProvider.System).TryBeginCompensationAsync(rows.Select(row =>
            new StorageMoveClaim(row.OperationId, Claim(row).Evidence with { State = "SourceDeleted" })).ToArray(), default));
    }

    [Fact]
    public async Task Copy_SecondFileAcknowledgmentLost_PreservesPreviouslyConfirmedAndUncertainPromotions()
    {
        await using var context = await ContextAsync();
        var cloud = new ControlledCloud { LoseCopyAcknowledgment = true, LostCopyCall = 2 };
        var prefix = Prefix();

        await Assert.ThrowsAsync<UploadOutcomeUnknownException>(() => Service(context, cloud).UploadAsync("private", prefix,
            [new ControlledFile("first.stl"), new ControlledFile("second.stl")], default));

        Assert.Equal(2, cloud.CopyCalls);
        Assert.Equal(0, cloud.SignCalls);
        Assert.Equal(3, cloud.Objects.Count); // First destination plus second quarantine and uncertain destination.
        Assert.Equal(2, cloud.Objects.Count(item => item.Value == 31));
        Assert.DoesNotContain(cloud.DeleteAttempts, item => item.Generation == 31 || item.Generation is null);
        Assert.False(await context.Uploads.AnyAsync(row => row.Name.StartsWith(prefix)));
    }

    [Fact]
    public async Task Signing_SecondFileDefiniteFailureBeforeMetadata_CompensatesAllConfirmedPromotions()
    {
        await using var context = await ContextAsync();
        var cloud = new ControlledCloud { FailSigning = true, FailedSigningCall = 2, AllowControlledSigning = true };
        var prefix = Prefix();

        await Record.ExceptionAsync(() => Service(context, cloud).UploadAsync("private", prefix,
            [new ControlledFile("first.stl"), new ControlledFile("second.stl")], default));

        Assert.Equal(2, cloud.SignCalls);
        Assert.Equal(2, cloud.CopyCalls);
        Assert.False(await context.Uploads.AnyAsync(row => row.Name.StartsWith(prefix)));
        Assert.Equal(2, cloud.DeleteAttempts.Count(item => item.Generation == 31));
        Assert.Empty(cloud.Objects);
        Assert.DoesNotContain(cloud.DeleteAttempts, item => item.Generation is null);
    }

    [Fact]
    public async Task Scan_ReopenedCallerStreamChangesBytes_UploadsAndScansOneImmutableSnapshot()
    {
        await using var context = await ContextAsync();
        var cloud = new ControlledCloud { FailSigning = true };
        var scanner = new ControlledScanner();
        var file = new ChangingFile();

        await Record.ExceptionAsync(() => Service(context, cloud, scanner).UploadAsync("private", Prefix(), [file], default));

        Assert.Equal(1, cloud.UploadCalls);
        Assert.Equal(new byte[] { 1 }, Assert.Single(cloud.UploadedBytes).Value);
        Assert.Equal(new byte[] { 1 }, Assert.Single(scanner.ScannedBytes));
        Assert.Equal(1, file.Opens);
        Assert.Equal(1, cloud.CopyCalls); // Original immutable [1] is clean; never promote bytes scanned only as [2].
        Assert.Equal(1, cloud.SignCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task Snapshot_DeclaredLengthMismatch_RejectsBeforeInitialProviderRpc(int actualLength)
    {
        await using var context = await ContextAsync();
        var cloud = new ControlledCloud { FailSigning = true };
        var scanner = new ControlledScanner();
        var file = new SuppliedStreamFile(1, () => new MemoryStream(new byte[actualLength]));

        var failure = await Record.ExceptionAsync(() => Service(context, cloud, scanner).UploadAsync("private", Prefix(), [file], default));

        Assert.NotNull(failure);
        Assert.Equal(0, cloud.UploadCalls);
        Assert.Equal(0, scanner.Calls);
        Assert.Equal(0, cloud.CopyCalls);
        Assert.Equal(0, cloud.SignCalls);
        Assert.Empty(cloud.DeleteAttempts);
    }

    [Fact]
    public async Task Snapshot_ScannerGetsFreshReadOnlyStreamsOverSameCapturedBytes()
    {
        await using var context = await ContextAsync();
        var cloud = new ControlledCloud { FailSigning = true };
        var scanner = new ControlledScanner { InspectFreshStreams = true };
        var file = new SuppliedStreamFile(1, () => new MemoryStream([1]));

        await Record.ExceptionAsync(() => Service(context, cloud, scanner).UploadAsync("private", Prefix(), [file], default));

        Assert.Equal(1, cloud.UploadCalls);
        Assert.Equal(1, file.Opens);
        Assert.Equal(new byte[] { 1 }, Assert.Single(scanner.ScannedBytes));
        Assert.Equal(new byte[] { 1 }, scanner.SecondReadBytes);
        Assert.Equal(0, scanner.SecondInitialPosition);
        Assert.False(scanner.FirstCanWrite);
        Assert.False(scanner.SecondCanWrite);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Snapshot_AggregateCap_ExactBoundaryAdmittedAndOverBoundaryHasNoRpc(bool over)
    {
        await using var context = await ContextAsync();
        var length = FileApplicationService.MaximumUploadBytes + (over ? 1 : 0);
        var cloud = new ControlledCloud { AllowControlledSigning = true, DiscardByteCapture = true };
        var scanner = new ControlledScanner { DiscardByteCapture = true };
        var file = new SuppliedStreamFile(length, () => new GeneratedZeroStream(length));

        var failure = await Record.ExceptionAsync(() => Service(context, cloud, scanner).UploadAsync("private", Prefix(), [file], default));

        if (over)
        {
            Assert.IsType<FileUploadValidationException>(failure);
            Assert.Equal(0, file.Opens);
            Assert.Equal(0, cloud.UploadCalls);
            Assert.Equal(0, scanner.Calls);
        }
        else
        {
            Assert.Null(failure);
            Assert.Equal(1, cloud.UploadCalls);
            Assert.Equal(1, scanner.Calls);
            Assert.Equal(1, cloud.CopyCalls);
            Assert.Equal(1, cloud.SignCalls);
        }
    }

    [Fact]
    public async Task Snapshot_CallerAbortDuringCapture_NeverStartsProviderRpc()
    {
        await using var context = await ContextAsync();
        using var canceled = new CancellationTokenSource();
        var enteredRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cloud = new ControlledCloud { AllowControlledSigning = true };
        var file = new SuppliedStreamFile(1, () => new CancelableCaptureStream(enteredRead));
        var service = Service(context, cloud);
        var upload = service.UploadAsync("private", Prefix(), [file], canceled.Token);
        await enteredRead.Task.WaitAsync(TimeSpan.FromSeconds(10));
        canceled.Cancel();

        var failure = Assert.IsAssignableFrom<OperationCanceledException>(
            await Record.ExceptionAsync(() => upload.WaitAsync(TimeSpan.FromSeconds(10))));
        Assert.Equal(canceled.Token, failure.CancellationToken);
        Assert.Equal(0, cloud.UploadCalls);
        Assert.Equal(0, cloud.CopyCalls);
        Assert.Equal(0, cloud.SignCalls);
        Assert.Empty(cloud.DeleteAttempts);
        Assert.Equal(1, file.Opens);
        await service.UploadAsync("private", Prefix(), Files(), default);
        Assert.Equal(1, cloud.UploadCalls); // Canceled capture releases admission for the next upload.
    }

    [Fact]
    public async Task Snapshot_KeyedProductionHttp_DurableDigestSdkAndScannerBindOneCapturedStream()
    {
        await using var context = await ContextAsync();
        await using var redis = new ContainerBuilder("redis:8-alpine").WithPortBinding(6379, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379)).Build();
        await redis.StartAsync();
        using var connection = await ConnectionMultiplexer.ConnectAsync($"{redis.Hostname}:{redis.GetMappedPublicPort(6379)},abortConnect=false");
        var opens = 0;
        var caller = new Mock<IFormFile>(MockBehavior.Strict);
        caller.SetupGet(file => file.Name).Returns("files");
        caller.SetupGet(file => file.FileName).Returns("part.stl");
        caller.SetupGet(file => file.ContentType).Returns("model/stl");
        caller.SetupGet(file => file.Length).Returns(1);
        caller.SetupGet(file => file.Headers).Returns(new HeaderDictionary());
        caller.Setup(file => file.OpenReadStream()).Returns(() => new MemoryStream([(byte)++opens]));
        var cloud = new ControlledCloud { AllowControlledSigning = true };
        var scanner = new ControlledScanner();
        using var factory = new RecoveryFactory(context.Database.GetConnectionString()!, cloud, connection, caller.Object, scanner);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Token("create"));
        var workflowKey = Guid.NewGuid().ToString("N");
        client.DefaultRequestHeaders.Add("Idempotency-Key", workflowKey);
        var path = Prefix();
        using var body = Multipart();
        using var first = await client.PostAsync("/Uploads?bucket=private&path=" + path, body);
        var firstOpens = opens;
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("recovery-fixture\n" + workflowKey)));
        var checkpoint = await connection.GetDatabase().StringGetAsync("legacy:file:idempotency:v1:" + identity);
        using var durable = JsonDocument.Parse((string)checkpoint!);
        // Independent literal canonical field bytes plus original content byte1, not production fingerprint helper.
        var fieldBytes = Encoding.UTF8.GetBytes("private\0" + path + "\0part.stl\0model/stl\0" + "1\0");
        var expectedDigest = Convert.ToHexString(SHA256.HashData([.. fieldBytes, 1]));
        using var changedBody = Multipart();
        using var changed = await client.PostAsync("/Uploads?bucket=private&path=" + path, changedBody);

        Assert.True(first.StatusCode == HttpStatusCode.Created, await first.Content.ReadAsStringAsync()); // Fixed public errors only; controlled SDK evidence.
        Assert.Equal(expectedDigest, durable.RootElement.GetProperty("fingerprint").GetString());
        Assert.Equal("completed", durable.RootElement.GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        Assert.Equal(1, cloud.UploadCalls);
        Assert.Equal(1, cloud.CopyCalls);
        Assert.Equal(1, cloud.SignCalls);
        Assert.Equal(new byte[] { 1 }, Assert.Single(cloud.UploadedBytes).Value);
        Assert.Equal(new byte[] { 1 }, Assert.Single(scanner.ScannedBytes));
        Assert.Equal(1, firstOpens);
        Assert.Equal(2, opens); // Exactly one initial and one changed-payload capture.
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task Snapshot_MalformedSecondFile_EntireBatchHasZeroProviderEffects(int actualLength)
    {
        await using var context = await ContextAsync();
        var cloud = new ControlledCloud { AllowControlledSigning = true };
        var scanner = new ControlledScanner();
        var malformed = new SuppliedStreamFile(1, () => new MemoryStream(new byte[actualLength]));

        Assert.NotNull(await Record.ExceptionAsync(() => Service(context, cloud, scanner)
            .UploadAsync("private", Prefix(), [new ControlledFile("first.stl"), malformed], default)));

        Assert.Equal(0, cloud.UploadCalls);
        Assert.Equal(0, scanner.Calls);
        Assert.Equal(0, cloud.CopyCalls);
        Assert.Equal(0, cloud.SignCalls);
        Assert.Empty(cloud.DeleteAttempts);
    }

    [Fact]
    public async Task Snapshot_AbortedSecondFile_EntireBatchHasZeroProviderEffectsAndPropagatesCallerAbort()
    {
        await using var context = await ContextAsync();
        using var canceled = new CancellationTokenSource();
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cloud = new ControlledCloud { AllowControlledSigning = true };
        var scanner = new ControlledScanner();
        var file = new SuppliedStreamFile(1, () => new CancelableCaptureStream(read));
        var task = Service(context, cloud, scanner).UploadAsync("private", Prefix(),
            [new ControlledFile("first.stl"), file], canceled.Token);
        await read.Task.WaitAsync(TimeSpan.FromSeconds(10));
        canceled.Cancel();

        var failure = Assert.IsAssignableFrom<OperationCanceledException>(
            await Record.ExceptionAsync(() => task.WaitAsync(TimeSpan.FromSeconds(10))));
        Assert.Equal(canceled.Token, failure.CancellationToken);
        Assert.Equal(0, cloud.UploadCalls);
        Assert.Equal(0, scanner.Calls);
        Assert.Equal(0, cloud.CopyCalls);
        Assert.Empty(cloud.DeleteAttempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Snapshot_KeyedReplayOrStoreFailure_ReleasesBatchForNextRequest(bool loseAcquire)
    {
        await using var context = await ContextAsync();
        await using var redis = new ContainerBuilder("redis:8-alpine").WithPortBinding(6379, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379)).Build();
        await redis.StartAsync();
        using var connection = await ConnectionMultiplexer.ConnectAsync($"{redis.Hostname}:{redis.GetMappedPublicPort(6379)},abortConnect=false");
        var opens = 0;
        var caller = new Mock<IFormFile>(MockBehavior.Strict);
        caller.SetupGet(file => file.Name).Returns("files");
        caller.SetupGet(file => file.FileName).Returns("part.stl");
        caller.SetupGet(file => file.ContentType).Returns("model/stl");
        caller.SetupGet(file => file.Length).Returns(1);
        caller.SetupGet(file => file.Headers).Returns(new HeaderDictionary());
        caller.Setup(file => file.OpenReadStream()).Returns(() => { opens++; return new MemoryStream([1]); });
        var cloud = new ControlledCloud { AllowControlledSigning = true };
        var checkpoint = new FaultOnceAcquireStore(new RedisUploadIdempotencyStore(connection), loseAcquire);
        using var factory = new RecoveryFactory(context.Database.GetConnectionString()!, cloud, connection, caller.Object,
            checkpointStore: checkpoint);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Token("create"));
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var path = Prefix();
        using var firstBody = Multipart();
        using var first = await client.PostAsync("/Uploads?bucket=private&path=" + path, firstBody);
        using var secondBody = Multipart();
        using var second = await client.PostAsync("/Uploads?bucket=private&path=" + path, secondBody);
        var opensAfterSecond = opens;
        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        using var thirdBody = Multipart();
        using var third = await client.PostAsync("/Uploads?bucket=private&path=" + Prefix(), thirdBody);

        Assert.Equal(loseAcquire ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(HttpStatusCode.Created, third.StatusCode);
        Assert.Equal(2, cloud.UploadCalls); // One initial/retry and one new unkeyed upload; replay/store fault do not upload.
        Assert.Equal(2, opensAfterSecond);
        Assert.Equal(3, opens);
    }

    [Fact]
    public async Task Snapshot_ProductionHttpBusySlot_DeniesBeforeRpcAndReusesAfterRelease()
    {
        await using var context = await ContextAsync();
        var enteredProvider = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProvider = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cloud = new ControlledCloud { FailSigning = true };
        cloud.BeforeUpload = async _ =>
        {
            if (cloud.UploadCalls == 1)
            {
                enteredProvider.TrySetResult();
                await releaseProvider.Task.WaitAsync(TimeSpan.FromSeconds(20));
            }
        };
        using var factory = new RecoveryFactory(context.Database.GetConnectionString()!, cloud);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Token("create"));
        using var firstBody = Multipart();
        var first = client.PostAsync("/Uploads?bucket=private&path=" + Prefix(), firstBody);
        await enteredProvider.Task.WaitAsync(TimeSpan.FromSeconds(10));
        HttpStatusCode busyStatus;
        int callsWhileBusy;
        try
        {
            using var busyBody = Multipart();
            using var busy = await client.PostAsync("/Uploads?bucket=private&path=" + Prefix(), busyBody);
            busyStatus = busy.StatusCode;
            callsWhileBusy = cloud.UploadCalls;
        }
        finally { releaseProvider.TrySetResult(); }
        using var firstResponse = await first.WaitAsync(TimeSpan.FromSeconds(10));
        using var reusedBody = Multipart();
        using var reused = await client.PostAsync("/Uploads?bucket=private&path=" + Prefix(), reusedBody);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, busyStatus);
        Assert.Equal(1, callsWhileBusy);
        Assert.Equal(2, cloud.UploadCalls); // First and released-slot reuse only; busy request must not upload.
    }

    [Fact]
    public async Task Snapshot_ProductionHttp_ReleasesBatchBeforeRequiredSigning()
    {
        await using var context = await ContextAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cloud = new ControlledCloud { AllowControlledSigning = true };
        cloud.BeforeSigning = async () =>
        {
            if (cloud.SignCalls == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(20));
            }
        };
        using var factory = new RecoveryFactory(context.Database.GetConnectionString()!, cloud);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Token("create"));
        using var firstBody = Multipart();
        var first = client.PostAsync("/Uploads?bucket=private&path=" + Prefix(), firstBody);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        HttpStatusCode secondStatus;
        try
        {
            using var secondBody = Multipart();
            using var second = await client.PostAsync("/Uploads?bucket=private&path=" + Prefix(), secondBody);
            secondStatus = second.StatusCode;
        }
        finally { release.TrySetResult(); }
        using var firstResponse = await first.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(HttpStatusCode.Created, secondStatus);
        Assert.True(firstResponse.StatusCode == HttpStatusCode.Created, await firstResponse.Content.ReadAsStringAsync());
        Assert.Equal(2, cloud.UploadCalls);
        Assert.Equal(2, cloud.CopyCalls);
        Assert.Equal(2, cloud.SignCalls);
    }

    [Theory]
    [InlineData("state-capacity")]
    [InlineData("constraint-definition")]
    public async Task RecoveryShape_ChangedPhysicalContract_RefusesBeforeInitialProviderRpc(string drift)
    {
        await using var context = await ContextAsync();
        await using var transaction = await context.Database.BeginTransactionAsync();
        try
        {
            if (drift == "state-capacity")
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"StorageMoveJournal\" ALTER COLUMN \"State\" TYPE varchar(8) USING left(\"State\",8)");
            else
            {
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"StorageMoveJournal\" DROP CONSTRAINT \"CK_StorageMoveJournal_SourceGeneration\"");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"StorageMoveJournal\" ADD CONSTRAINT \"CK_StorageMoveJournal_SourceGeneration\" CHECK (TRUE)");
            }
            var cloud = new ControlledCloud { AllowControlledSigning = true };

            Assert.NotNull(await Record.ExceptionAsync(() => Service(context, cloud).UploadAsync("private", Prefix(), Files(), default)));

            Assert.Equal(0, cloud.UploadCalls);
            Assert.Equal(0, cloud.CopyCalls);
            Assert.Equal(0, cloud.SignCalls);
        }
        finally { await transaction.RollbackAsync(); }
    }

    [Theory]
    [InlineData("CompensationPending")]
    [InlineData("CompensatedRemoved")]
    [InlineData("CompensatedAbsent")]
    [InlineData("CompensationUnknown")]
    [InlineData("MetadataSubmitting")]
    public async Task Compensation_ExistingUnknownCheckpoint_CannotOverwriteRetainedDisposition(string state)
    {
        await using var context = await ContextAsync();
        var row = RecoveryRow(state);
        context.StorageMoveJournals.Add(row);
        await context.SaveChangesAsync();
        var modified = (await context.StorageMoveJournals.AsNoTracking().SingleAsync(item => item.OperationId == row.OperationId)).ModifiedAt;

        await new StorageMoveJournalRepository(context, TimeProvider.System).UnknownAsync(row.OperationId, default);

        var after = await context.StorageMoveJournals.AsNoTracking().SingleAsync(item => item.OperationId == row.OperationId);
        Assert.Equal(state, after.State);
        Assert.Equal(modified, after.ModifiedAt);
        Assert.Equal(31, after.DestinationGeneration);
        Assert.Equal(row.SourceObjectName, after.SourceObjectName);
    }

    [Fact]
    public async Task Metadata_Submission_IsDurablyFencedForEveryPromotionBeforeRepositoryCall()
    {
        await using var context = await ContextAsync();
        var prefix = Prefix();
        var observed = Array.Empty<string>();
        var repository = new ObservedMetadataSubmission(new UploadRepository(context, TimeProvider.System), async () =>
        {
            observed = await context.StorageMoveJournals.AsNoTracking().Where(row => row.DestinationObjectName.StartsWith(prefix))
                .OrderBy(row => row.DestinationObjectName).Select(row => row.State).ToArrayAsync();
        });
        var cloud = new ControlledCloud { AllowControlledSigning = true };

        await Service(context, cloud, repository: repository).UploadAsync("private", prefix,
            [new ControlledFile("first.stl"), new ControlledFile("second.stl")], default);

        Assert.Equal(new[] { "MetadataSubmitting", "MetadataSubmitting" }, observed);
        Assert.Equal(1, repository.Calls);
        Assert.Equal(2, await context.Uploads.CountAsync(row => row.Name.StartsWith(prefix)));
    }

    [Fact]
    public async Task Metadata_MissingBatchEvidence_RefusesSubmissionAndPreservesEveryPromotion()
    {
        await using var context = await ContextAsync();
        var prefix = Prefix();
        var cloud = new ControlledCloud { AllowControlledSigning = true };
        cloud.BeforeSigning = async () =>
        {
            if (cloud.SignCalls == 1)
            {
                var first = await context.StorageMoveJournals.AsNoTracking().Where(row => row.DestinationObjectName.StartsWith(prefix))
                    .OrderBy(row => row.DestinationObjectName).Select(row => row.OperationId).FirstAsync();
                await context.StorageMoveJournals.Where(row => row.OperationId == first).ExecuteDeleteAsync();
            }
        };
        var repository = new ObservedMetadataSubmission(new UploadRepository(context, TimeProvider.System), () => Task.CompletedTask);

        Assert.NotNull(await Record.ExceptionAsync(() => Service(context, cloud, repository: repository)
            .UploadAsync("private", prefix, [new ControlledFile("first.stl"), new ControlledFile("second.stl")], default)));

        Assert.Equal(0, repository.Calls);
        Assert.False(await context.Uploads.AnyAsync(row => row.Name.StartsWith(prefix)));
        Assert.Equal(2, cloud.Objects.Count);
        Assert.DoesNotContain(cloud.DeleteAttempts, item => item.Generation == 31);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProductionHttp_PutAmbiguousCommittedEvidence_RefusesWithoutChoosingLatest(bool conflicting)
    {
        await using var context = await ContextAsync();
        var source = Prefix() + "/proven.stl";
        var destination = Prefix() + "/moved.stl";
        context.Uploads.Add(new Upload { Bucket = "private", Name = source, ContentType = "model/stl", Size = 1 });
        var first = RecoveryRow("MetadataCommitted"); first.DestinationObjectName = source;
        var second = RecoveryRow(conflicting ? "Unknown" : "MetadataCommitted"); second.DestinationObjectName = source;
        if (conflicting) second.DestinationGeneration = 47;
        context.StorageMoveJournals.AddRange(first, second);
        await context.SaveChangesAsync();
        var cloud = new ControlledCloud { ExpectedCopySourceGeneration = 31 };
        cloud.Objects[source] = 31;
        using var factory = new RecoveryFactory(context.Database.GetConnectionString()!, cloud);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Token("update"));

        using var response = await client.PutAsync($"/Uploads?sourceBucket=private&sourceObjectName={source}&destinationBucket=private&destinationObjectName={destination}", null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, cloud.CopyCalls);
        Assert.Empty(cloud.DeleteAttempts);
        Assert.Equal(31, cloud.Objects[source]);
        Assert.True(await context.Uploads.AsNoTracking().AnyAsync(row => row.Name == source));
        Assert.Equal(2, await context.StorageMoveJournals.CountAsync(row => row.DestinationObjectName == source || row.DestinationObjectName == destination));
    }

    [Fact]
    public async Task Compensation_CompleteBatchPendingClaim_IsCommittedBeforeAnyDestinationDelete()
    {
        await using var context = await ContextAsync();
        var prefix = Prefix();
        var observations = new List<string[]>();
        var cloud = new ControlledCloud { FailSigning = true };
        cloud.BeforeDestinationCleanup = async () => observations.Add(await context.StorageMoveJournals.AsNoTracking()
            .Where(row => row.DestinationObjectName.StartsWith(prefix)).OrderBy(row => row.DestinationObjectName)
            .Select(row => row.State).ToArrayAsync());

        Assert.NotNull(await Record.ExceptionAsync(() => Service(context, cloud).UploadAsync("private", prefix,
            [new ControlledFile("first.stl"), new ControlledFile("second.stl")], default)));

        Assert.Equal(2, observations.Count);
        Assert.Equal(new[] { "CompensationPending", "CompensationPending" }, observations[0]);
        Assert.Equal(2, cloud.DeleteAttempts.Count(item => item.Generation == 31));
        Assert.Empty(cloud.Objects);
        Assert.False(await context.Uploads.AnyAsync(row => row.Name.StartsWith(prefix)));
    }

    [Theory]
    [InlineData("removed", "CompensatedRemoved")]
    [InlineData("absent", "CompensatedAbsent")]
    [InlineData("lost-ack", "CompensationUnknown")]
    [InlineData("replacement", "CompensationUnknown")]
    public async Task Compensation_Disposition_DistinguishesRemovedAbsentAndUnknownWithoutGenerationDrift(string outcome, string state)
    {
        await using var context = await ContextAsync();
        var prefix = Prefix();
        var cloud = new ControlledCloud
        {
            FailSigning = true,
            RemoveBeforeSigning = outcome == "absent",
            LoseDestinationCleanupAcknowledgment = outcome == "lost-ack",
            ReplaceBeforeSigning = outcome == "replacement",
        };

        var failure = await Record.ExceptionAsync(() => Service(context, cloud).UploadAsync("private", prefix, Files(), default));

        Assert.NotNull(failure);
        var row = await context.StorageMoveJournals.AsNoTracking().SingleAsync(item => item.DestinationObjectName.StartsWith(prefix));
        Assert.Equal(state, row.State);
        Assert.Equal(31, row.DestinationGeneration);
        Assert.Single(cloud.DeleteAttempts, item => item.Generation == 31);
        Assert.DoesNotContain(cloud.DeleteAttempts, item => item.Generation is null);
        Assert.False(await context.Uploads.AnyAsync(item => item.Name.StartsWith(prefix)));
        if (outcome == "replacement") Assert.Equal(47, Assert.Single(cloud.Objects).Value);
        else Assert.Empty(cloud.Objects);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Compensation_MissingOrMetadataCommittedBatchMember_PreservesAllDestinations(bool committed)
    {
        await using var context = await ContextAsync();
        var prefix = Prefix();
        var cloud = new ControlledCloud { FailSigning = true };
        cloud.BeforeSigning = async () =>
        {
            var id = await context.StorageMoveJournals.AsNoTracking().Where(row => row.DestinationObjectName.StartsWith(prefix))
                .OrderBy(row => row.DestinationObjectName).Select(row => row.OperationId).FirstAsync();
            if (committed)
                await context.StorageMoveJournals.Where(row => row.OperationId == id).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.State, "MetadataCommitted"));
            else await context.StorageMoveJournals.Where(row => row.OperationId == id).ExecuteDeleteAsync();
        };

        Assert.NotNull(await Record.ExceptionAsync(() => Service(context, cloud).UploadAsync("private", prefix,
            [new ControlledFile("first.stl"), new ControlledFile("second.stl")], default)));

        Assert.DoesNotContain(cloud.DeleteAttempts, item => item.Generation == 31);
        Assert.Equal(2, cloud.Objects.Count);
        Assert.All(cloud.Objects.Values, value => Assert.Equal(31, value));
        Assert.False(await context.Uploads.AnyAsync(row => row.Name.StartsWith(prefix)));
        if (committed) Assert.True(await context.StorageMoveJournals.AnyAsync(row => row.DestinationObjectName.StartsWith(prefix) && row.State == "MetadataCommitted"));
    }

    [Fact]
    public async Task Compensation_TwoPostgreSqlContexts_MetadataAndCompensationClaimsHaveExactlyOneWinner()
    {
        await using var seed = await ContextAsync();
        var rows = new[] { RecoveryRow("SourceDeleted"), RecoveryRow("SourceDeleted") };
        seed.StorageMoveJournals.AddRange(rows); await seed.SaveChangesAsync();
        var claims = rows.Select(Claim).ToArray();
        await using var metadataContext = fixture.CreateContext();
        await using var compensationContext = fixture.CreateContext();

        var results = await Task.WhenAll(
            new StorageMoveJournalRepository(metadataContext, TimeProvider.System).TryBeginMetadataSubmissionAsync(claims, default),
            new StorageMoveJournalRepository(compensationContext, TimeProvider.System).TryBeginCompensationAsync(claims.Reverse().ToArray(), default));

        Assert.Single(results, result => result);
        var ids = rows.Select(row => row.OperationId).ToArray();
        var after = await seed.StorageMoveJournals.AsNoTracking().Where(row => ids.Contains(row.OperationId)).ToArrayAsync();
        Assert.All(after, row => Assert.Equal(results[0] ? "MetadataSubmitting" : "CompensationPending", row.State));
        Assert.All(after, row => Assert.Equal(31, row.DestinationGeneration));
        Assert.False(await new StorageMoveJournalRepository(seed, TimeProvider.System).TryBeginCompensationAsync(claims, default));
    }

    [Theory]
    [InlineData(false, "transient")]
    [InlineData(true, "transient")]
    [InlineData(true, "invalid-operation")]
    [InlineData(true, "cancellation")]
    public async Task Compensation_ConfiguredRetryTransientCommitFailure_IsNeverReplayed(bool lostAcknowledgment, string failureKind)
    {
        await using var seed = await ContextAsync();
        var rows = new[] { RecoveryRow("SourceDeleted"), RecoveryRow("SourceDeleted") };
        seed.StorageMoveJournals.AddRange(rows); await seed.SaveChangesAsync();
        var claims = rows.Select(Claim).ToArray();
        var fault = new ClaimCommitFault(lostAcknowledgment, failureKind);
        await using var faulted = new FileDbContext(new DbContextOptionsBuilder<FileDbContext>()
            .UseNpgsql(seed.Database.GetConnectionString(), options => options.EnableRetryOnFailure(5, TimeSpan.FromSeconds(10), null))
            .AddInterceptors(fault).Options);

        var failure = await Assert.ThrowsAsync<UploadOutcomeUnknownException>(() =>
            new StorageMoveJournalRepository(faulted, TimeProvider.System).TryBeginCompensationAsync(claims, default));

        Assert.Same(fault.Failure, failure.InnerException);
        Assert.Equal(1, fault.Calls);
        var ids = rows.Select(row => row.OperationId).ToArray();
        var after = await seed.StorageMoveJournals.AsNoTracking().Where(row => ids.Contains(row.OperationId)).ToArrayAsync();
        Assert.All(after, row => Assert.Equal(lostAcknowledgment ? "CompensationPending" : "SourceDeleted", row.State));
        Assert.All(after, row => Assert.Equal(31, row.DestinationGeneration));
        if (lostAcknowledgment)
            Assert.False(await new StorageMoveJournalRepository(seed, TimeProvider.System).TryBeginCompensationAsync(claims, default));
    }

    [Theory]
    [InlineData(false, "transient")]
    [InlineData(true, "transient")]
    [InlineData(true, "invalid-operation")]
    [InlineData(true, "cancellation")]
    public async Task Compensation_UncertainConfiguredClaim_PreservesEveryObjectWithoutCleanupOrMetadata(bool lostAcknowledgment, string failureKind)
    {
        await using var seed = await ContextAsync();
        var prefix = Prefix();
        var fault = new ClaimCommitFault(lostAcknowledgment, failureKind);
        await using var faulted = new FileDbContext(new DbContextOptionsBuilder<FileDbContext>()
            .UseNpgsql(seed.Database.GetConnectionString(), options => options.EnableRetryOnFailure(5, TimeSpan.FromSeconds(10), null))
            .AddInterceptors(fault).Options);
        var cloud = new ControlledCloud { FailSigning = true };

        var failure = await Assert.ThrowsAsync<UploadRollbackException>(() => Service(faulted, cloud).UploadAsync("private", prefix,
            [new ControlledFile("first.stl"), new ControlledFile("second.stl")], default));

        Assert.Equal(1, fault.Calls);
        Assert.Contains(failure.CleanupFailures, item => item.Cause is UploadOutcomeUnknownException outcome
            && ReferenceEquals(outcome.InnerException, fault.Failure));
        Assert.Equal(2, cloud.DeleteAttempts.Count(item => item.Generation == 17));
        Assert.DoesNotContain(cloud.DeleteAttempts, item => item.Generation != 17);
        Assert.Equal(2, cloud.Objects.Count);
        Assert.All(cloud.Objects.Values, item => Assert.Equal(31, item));
        Assert.False(await seed.Uploads.AnyAsync(row => row.Name.StartsWith(prefix)));
        var after = await seed.StorageMoveJournals.AsNoTracking().Where(row => row.DestinationObjectName.StartsWith(prefix)).ToArrayAsync();
        Assert.Equal(2, after.Length);
        Assert.All(after, row => Assert.Equal(lostAcknowledgment ? "CompensationPending" : "SourceDeleted", row.State));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Compensation_PostgreSqlCommitBoundaryFailure_RollsBackOrRetainsUnknownCommittedClaim(bool lostAcknowledgment)
    {
        await using var seed = await ContextAsync();
        var rows = new[] { RecoveryRow("SourceDeleted"), RecoveryRow("SourceDeleted") };
        seed.StorageMoveJournals.AddRange(rows); await seed.SaveChangesAsync();
        var claims = rows.Select(Claim).ToArray();
        var fault = new ClaimCommitFault(lostAcknowledgment);
        await using var faulted = new FileDbContext(new DbContextOptionsBuilder<FileDbContext>()
            .UseNpgsql(seed.Database.GetConnectionString()).AddInterceptors(fault).Options);

        var failure = await Record.ExceptionAsync(() => new StorageMoveJournalRepository(faulted, TimeProvider.System).TryBeginCompensationAsync(claims, default));

        Assert.NotNull(failure);
        Assert.Equal(1, fault.Calls);
        Assert.Same(fault.Failure, failure is UploadOutcomeUnknownException outcome ? outcome.InnerException : failure);
        var ids = rows.Select(row => row.OperationId).ToArray();
        var after = await seed.StorageMoveJournals.AsNoTracking().Where(row => ids.Contains(row.OperationId)).ToArrayAsync();
        Assert.All(after, row => Assert.Equal(lostAcknowledgment ? "CompensationPending" : "SourceDeleted", row.State));
        Assert.All(after, row => Assert.Equal(31, row.DestinationGeneration));
        if (lostAcknowledgment)
            Assert.False(await new StorageMoveJournalRepository(seed, TimeProvider.System).TryBeginCompensationAsync(claims, default));
    }

    [Fact]
    public async Task Compensation_CanceledCaller_DoesNotClaimAndPreservesEveryRow()
    {
        await using var context = await ContextAsync();
        var row = RecoveryRow("SourceDeleted"); context.StorageMoveJournals.Add(row); await context.SaveChangesAsync();
        using var canceled = new CancellationTokenSource(); canceled.Cancel();

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new StorageMoveJournalRepository(context, TimeProvider.System).TryBeginCompensationAsync([Claim(row)], canceled.Token));

        Assert.Equal(canceled.Token, failure.CancellationToken);
        Assert.Equal("SourceDeleted", (await context.StorageMoveJournals.AsNoTracking().SingleAsync(item => item.OperationId == row.OperationId)).State);
    }

    [Fact]
    public async Task Compensation_MissingBatchClaim_IsFalseWithEveryExistingMemberUnchanged()
    {
        await using var context = await ContextAsync();
        var row = RecoveryRow("SourceDeleted"); context.StorageMoveJournals.Add(row); await context.SaveChangesAsync();
        var missing = RecoveryRow("SourceDeleted");
        var modified = (await context.StorageMoveJournals.AsNoTracking().SingleAsync(item => item.OperationId == row.OperationId)).ModifiedAt;

        Assert.False(await new StorageMoveJournalRepository(context, TimeProvider.System).TryBeginCompensationAsync([Claim(row), Claim(missing)], default));

        var after = await context.StorageMoveJournals.AsNoTracking().SingleAsync(item => item.OperationId == row.OperationId);
        Assert.Equal("SourceDeleted", after.State); Assert.Equal(modified, after.ModifiedAt);
    }

    [Fact]
    public async Task Compensation_DuplicateClaim_IsRejectedWithoutChangingAuthority()
    {
        await using var context = await ContextAsync();
        var row = RecoveryRow("SourceDeleted"); context.StorageMoveJournals.Add(row); await context.SaveChangesAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => new StorageMoveJournalRepository(context, TimeProvider.System)
            .TryBeginCompensationAsync([Claim(row), Claim(row)], default));

        Assert.Equal("SourceDeleted", (await context.StorageMoveJournals.AsNoTracking().SingleAsync(item => item.OperationId == row.OperationId)).State);
    }

    private static StorageMoveClaim Claim(StorageMoveJournal row) => new(row.OperationId,
        new StorageMoveEvidence(row.ScanClean, row.SourceBucket, row.SourceObjectName, row.SourceGeneration,
            row.DestinationBucket, row.DestinationObjectName, row.DestinationGeneration, row.State));

    private sealed class ClaimCommitFault(bool afterCommit, string failureKind = "io") : DbTransactionInterceptor
    {
        public int Calls { get; private set; }
        public Exception Failure { get; } = failureKind switch
        {
            "transient" => new Npgsql.NpgsqlException("controlled transient commit-boundary failure", new TimeoutException("controlled transport timeout")),
            "invalid-operation" => new InvalidOperationException("controlled post-commit failure"),
            "cancellation" => new OperationCanceledException("controlled post-commit cancellation"),
            _ => new IOException("controlled PostgreSQL commit-boundary failure"),
        };
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (!afterCommit) { Calls++; throw Failure; }
            return ValueTask.FromResult(result);
        }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (afterCommit) { Calls++; throw Failure; }
            return Task.CompletedTask;
        }
    }

    private static StorageMoveJournal RecoveryRow(string state) => new()
    {
        OperationId = Guid.NewGuid(),
        ScanClean = true,
        SourceBucket = "private",
        SourceObjectName = "_quarantine/" + Prefix(),
        SourceGeneration = 17,
        DestinationBucket = "private",
        DestinationObjectName = Prefix(),
        DestinationGeneration = 31,
        State = state,
        CreatedAt = DateTimeOffset.UtcNow,
        ModifiedAt = DateTimeOffset.UtcNow,
    };

    private static async Task<bool> IntentExistsAsync(FileDbContext context, string bucket, string objectName)
    {
        await context.Database.OpenConnectionAsync();
        await using var probe = context.Database.GetDbConnection().CreateCommand();
        probe.CommandText = "SELECT to_regclass('public.\"QuarantineUploadIntent\"') IS NOT NULL;";
        if (!(bool)(await probe.ExecuteScalarAsync())!) return false;
        probe.CommandText = "SELECT EXISTS (SELECT 1 FROM \"QuarantineUploadIntent\" WHERE \"Bucket\"=@bucket AND \"ObjectName\"=@name);";
        var bucketParameter = probe.CreateParameter(); bucketParameter.ParameterName = "bucket"; bucketParameter.Value = bucket;
        var nameParameter = probe.CreateParameter(); nameParameter.ParameterName = "name"; nameParameter.Value = objectName;
        probe.Parameters.Add(bucketParameter); probe.Parameters.Add(nameParameter);
        return (bool)(await probe.ExecuteScalarAsync())!;
    }

    private async Task<FileDbContext> ContextAsync()
    {
        var context = fixture.CreateContext();
        await context.Database.MigrateAsync();
        return context;
    }

    private static FileApplicationService Service(FileDbContext context, ControlledCloud cloud, ControlledScanner? scanner = null,
        IUploadRepository? repository = null, IQuarantineUploadIntent? intents = null)
    {
        var journal = new StorageMoveJournalRepository(context, TimeProvider.System);
        var options = Options.Create(new FileStorageOptions { Enabled = true, WritesEnabled = true, AllowedBuckets = ["private"] });
        return new FileApplicationService(cloud.Storage(journal), scanner ?? new ControlledScanner(),
            repository ?? new UploadRepository(context, TimeProvider.System), journal, new ObjectNamePolicy(options, TimeProvider.System),
            options, new LegacyFileRuntimeGate(options), NullLogger<FileApplicationService>.Instance,
            intents ?? new QuarantineUploadIntentRepository(context, TimeProvider.System), new UploadSnapshotCapture(), journal);
    }

    private static string Prefix() => "recovery-" + Guid.NewGuid().ToString("N");
    private static IReadOnlyList<IUploadFile> Files() => [new ControlledFile()];
    private static MultipartFormDataContent Multipart()
    {
        var body = new MultipartFormDataContent();
        body.Add(new ByteArrayContent([1]) { Headers = { ContentType = new MediaTypeHeaderValue("model/stl") } }, "files", "part.stl");
        return body;
    }

    private sealed class ControlledFile(string fileName = "part.stl") : IUploadFile
    {
        public string FileName => fileName;
        public string ContentType => "model/stl";
        public long Length => 1;
        public Stream OpenReadStream() => new MemoryStream([1]);
    }

    private sealed class ChangingFile : IUploadFile
    {
        private int opens;
        public int Opens => opens;
        public string FileName => "changing.stl";
        public string ContentType => "model/stl";
        public long Length => 1;
        public Stream OpenReadStream() => new MemoryStream([++opens == 1 ? (byte)1 : (byte)2]);
    }

    private sealed class SuppliedStreamFile(long length, Func<Stream> open) : IUploadFile
    {
        public int Opens { get; private set; }
        public string FileName => "snapshot.stl";
        public string ContentType => "model/stl";
        public long Length => length;
        public Stream OpenReadStream() { Opens++; return open(); }
    }

    private sealed class CancelableCaptureStream(TaskCompletionSource entered) : MemoryStream(new byte[1])
    {
        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken token)
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return 0;
        }
    }

    // Generated synthetic bytes keep fixture overhead bounded; no customer data or disk spool.
    private sealed class GeneratedZeroStream(long length) : MemoryStream
    {
        private long remaining = length;
        public override long Length { get; } = length;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var count = (int)Math.Min(remaining, buffer.Length);
            buffer.Span[..count].Clear();
            remaining -= count;
            return ValueTask.FromResult(count);
        }
        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken token)
        {
            var buffer = new byte[Math.Min(bufferSize, 8192)];
            while (remaining > 0)
            {
                token.ThrowIfCancellationRequested();
                var count = (int)Math.Min(remaining, buffer.Length);
                await destination.WriteAsync(buffer.AsMemory(0, count), token);
                remaining -= count;
            }
        }
    }

    private sealed class ObservedMetadataSubmission(IUploadRepository inner, Func<Task> observe) : IUploadRepository
    {
        public int Calls { get; private set; }
        public async Task AddRangeAsync(IReadOnlyCollection<Upload> uploads, CancellationToken token)
        { Calls++; await observe(); await inner.AddRangeAsync(uploads, token); }
        public Task<bool> ExistsAsync(string bucket, string name, CancellationToken token) => inner.ExistsAsync(bucket, name, token);
        public Task DeleteAsync(string bucket, string name, CancellationToken token) => inner.DeleteAsync(bucket, name, token);
        public Task MoveAsync(string sourceBucket, string sourceName, string destinationBucket, string destinationName, CancellationToken token) =>
            inner.MoveAsync(sourceBucket, sourceName, destinationBucket, destinationName, token);
    }

    private sealed class CommitThenLoseAcknowledgment(IUploadRepository inner, CancellationTokenSource? cancel) : IUploadRepository
    {
        public IOException Failure { get; } = new("controlled metadata acknowledgment lost after PostgreSQL commit");
        public async Task AddRangeAsync(IReadOnlyCollection<Upload> uploads, CancellationToken token)
        {
            await inner.AddRangeAsync(uploads, token);
            cancel?.Cancel();
            throw Failure;
        }
        public Task<bool> ExistsAsync(string bucket, string name, CancellationToken token) => inner.ExistsAsync(bucket, name, token);
        public Task DeleteAsync(string bucket, string name, CancellationToken token) => inner.DeleteAsync(bucket, name, token);
        public Task MoveAsync(string sourceBucket, string sourceName, string destinationBucket, string destinationName, CancellationToken token) =>
            inner.MoveAsync(sourceBucket, sourceName, destinationBucket, destinationName, token);
    }

    private sealed class FaultingIntent(IQuarantineUploadIntent inner) : IQuarantineUploadIntent
    {
        public bool LoseAcknowledgment { get; init; }
        public bool FailUnknown { get; init; }
        public bool TimeoutUnknown { get; init; }
        public CancellationToken UnknownToken { get; private set; }
        public IOException Failure { get; } = new("controlled intent acknowledgment/checkpoint response lost");
        public Task PrepareAsync(Guid operationId, Guid parentOperationId, string bucket, string objectName,
            string contentType, long declaredSize, CancellationToken token) =>
            inner.PrepareAsync(operationId, parentOperationId, bucket, objectName, contentType, declaredSize, token);
        public async Task AcknowledgeAsync(Guid operationId, long generation, CancellationToken token)
        {
            await inner.AcknowledgeAsync(operationId, generation, token);
            if (LoseAcknowledgment) throw Failure;
        }
        public Task UnknownAsync(Guid operationId, CancellationToken token)
        {
            UnknownToken = token;
            if (FailUnknown) return Task.FromException(Failure);
            return TimeoutUnknown ? Task.Delay(Timeout.Infinite, token) : inner.UnknownAsync(operationId, token);
        }
    }

    private sealed class ControlledScanner : IFileSafetyScanner
    {
        public bool DiscardByteCapture { get; init; }
        public bool InspectFreshStreams { get; init; }
        public bool FirstCanWrite { get; private set; }
        public bool SecondCanWrite { get; private set; }
        public long SecondInitialPosition { get; private set; }
        public byte[]? SecondReadBytes { get; private set; }
        public int Calls { get; private set; }
        public List<byte[]> ScannedBytes { get; } = [];
        public async Task<FileSafetyResult> ScanAsync(IUploadFile file, CancellationToken cancellationToken)
        {
            Calls++;
            await using var bytes = file.OpenReadStream();
            FirstCanWrite = bytes.CanWrite;
            if (DiscardByteCapture)
            {
                await bytes.CopyToAsync(Stream.Null, cancellationToken);
                return new FileSafetyResult(FileSafetyVerdict.Clean);
            }
            using var consumed = new MemoryStream();
            await bytes.CopyToAsync(consumed, cancellationToken);
            ScannedBytes.Add(consumed.ToArray());
            if (InspectFreshStreams)
            {
                await using var second = file.OpenReadStream();
                SecondCanWrite = second.CanWrite;
                SecondInitialPosition = second.Position;
                using var secondConsumed = new MemoryStream();
                await second.CopyToAsync(secondConsumed, cancellationToken);
                SecondReadBytes = secondConsumed.ToArray();
            }
            return new FileSafetyResult(FileSafetyVerdict.Clean);
        }
    }

    private sealed class ControlledCloud
    {
        public bool DiscardByteCapture { get; init; }
        public Dictionary<string, long> Objects { get; } = [];
        public Dictionary<string, byte[]> UploadedBytes { get; } = [];
        public List<long> CopySourceGenerations { get; } = [];
        public long ExpectedCopySourceGeneration { get; init; } = 17;
        public List<(string Name, long? Generation, bool Canceled)> DeleteAttempts { get; } = [];
        public bool LoseUploadAcknowledgment { get; init; }
        public bool LoseCopyAcknowledgment { get; init; }
        public bool LoseDeleteAcknowledgment { get; init; }
        public bool FailSigning { get; init; }
        public bool AllowControlledSigning { get; init; }
        public int FailedSigningCall { get; init; } = 1;
        public int LostCopyCall { get; init; } = 1;
        public bool ReplaceBeforeSigning { get; init; }
        public bool RemoveBeforeSigning { get; init; }
        public bool LoseDestinationCleanupAcknowledgment { get; init; }
        public bool FailDestinationCleanup { get; init; }
        public CancellationTokenSource? CancelAtSigning { get; init; }
        public CancellationTokenSource? CancelAtOutcome { get; init; }
        public Func<StorageObject, Task>? BeforeUpload { get; set; }
        public Func<Task>? BeforeSigning { get; set; }
        public Func<Task>? BeforeDestinationCleanup { get; set; }
        public IOException SigningFailure { get; } = new("controlled required signing failure");
        public IOException CleanupFailure { get; } = new("controlled conditional cleanup failure");
        public int UploadCalls { get; private set; }
        public int CopyCalls { get; private set; }
        public int SignCalls { get; private set; }

        public GoogleCloudObjectStorage Storage(IStorageMoveJournal journal)
        {
            var client = new Mock<StorageClient>(MockBehavior.Strict);
            client.Setup(value => value.UploadObjectAsync(It.IsAny<StorageObject>(), It.IsAny<Stream>(),
                    It.IsAny<UploadObjectOptions>(), It.IsAny<CancellationToken>()))
                .Returns(new InvocationFunc(invocation => UploadControlledAsync(
                    (StorageObject)invocation.Arguments[0], (Stream)invocation.Arguments[1], (UploadObjectOptions)invocation.Arguments[2],
                    (CancellationToken)invocation.Arguments[3])));
            client.Setup(value => value.GetObjectAsync("private", It.IsAny<string>(), It.IsAny<GetObjectOptions>(), It.IsAny<CancellationToken>()))
                .Returns<string, string, GetObjectOptions, CancellationToken>((_, name, _, _) =>
                    Objects.TryGetValue(name, out var generation)
                        ? Task.FromResult(new StorageObject { Generation = generation, Size = 1 })
                        : Task.FromException<StorageObject>(ApiError(HttpStatusCode.NotFound)));
            client.Setup(value => value.CopyObjectAsync("private", It.IsAny<string>(), "private", It.IsAny<string>(),
                    It.IsAny<CopyObjectOptions>(), It.IsAny<CancellationToken>()))
                .Returns<string, string, string, string, CopyObjectOptions, CancellationToken>((_, source, _, destination, options, _) =>
                {
                    CopyCalls++;
                    Assert.Equal(ExpectedCopySourceGeneration, options.SourceGeneration);
                    Assert.Equal(ExpectedCopySourceGeneration, options.IfSourceGenerationMatch);
                    Assert.Equal(0, options.IfGenerationMatch);
                    Assert.Equal(ExpectedCopySourceGeneration, Objects[source]);
                    CopySourceGenerations.Add(options.SourceGeneration!.Value);
                    Objects.Add(destination, 31);
                    if (LoseCopyAcknowledgment && CopyCalls == LostCopyCall) CancelAtOutcome?.Cancel();
                    return LoseCopyAcknowledgment && CopyCalls == LostCopyCall ? Task.FromException<StorageObject>(new IOException("controlled copy acknowledgment lost"))
                        : Task.FromResult(new StorageObject { Generation = 31 });
                });
            client.Setup(value => value.DeleteObjectAsync("private", It.IsAny<string>(), It.IsAny<DeleteObjectOptions>(), It.IsAny<CancellationToken>()))
                .Returns<string, string, DeleteObjectOptions, CancellationToken>(async (_, name, options, token) =>
                {
                    DeleteAttempts.Add((name, options.IfGenerationMatch, token.IsCancellationRequested));
                    if (options.IfGenerationMatch == 31 && BeforeDestinationCleanup is not null) await BeforeDestinationCleanup();
                    if (options.IfGenerationMatch == 31 && FailDestinationCleanup) throw CleanupFailure;
                    if (!Objects.TryGetValue(name, out var generation)) throw ApiError(HttpStatusCode.NotFound);
                    if (options.IfGenerationMatch != generation) throw ApiError(HttpStatusCode.PreconditionFailed);
                    Objects.Remove(name);
                    if (LoseDeleteAcknowledgment && options.IfGenerationMatch == 17) CancelAtOutcome?.Cancel();
                    if ((LoseDeleteAcknowledgment && options.IfGenerationMatch == 17)
                        || (LoseDestinationCleanupAcknowledgment && options.IfGenerationMatch == 31))
                        throw new IOException("controlled delete acknowledgment lost");
                });
            var signer = new Mock<UrlSigner.IBlobSigner>(MockBehavior.Strict);
            signer.SetupGet(value => value.Id).Returns("controlled@example.invalid");
            signer.SetupGet(value => value.Algorithm).Returns("GOOG4-RSA-SHA256");
            signer.Setup(value => value.CreateSignatureAsync(It.IsAny<byte[]>(), It.IsAny<UrlSigner.BlobSignerParameters>(), It.IsAny<CancellationToken>()))
                .Returns(async () =>
                {
                    SignCalls++;
                    if (BeforeSigning is not null) await BeforeSigning();
                    if (ReplaceBeforeSigning)
                        foreach (var name in Objects.Where(item => item.Value == 31).Select(item => item.Key).ToArray()) Objects[name] = 47;
                    if (RemoveBeforeSigning)
                        foreach (var name in Objects.Where(item => item.Value == 31).Select(item => item.Key).ToArray()) Objects.Remove(name);
                    CancelAtSigning?.Cancel();
                    if (FailSigning && SignCalls == FailedSigningCall) throw SigningFailure;
                    // Controlled SDK acknowledgment only; never real-provider signing/readiness acceptance.
                    return AllowControlledSigning ? "AQ==" : throw new IOException("unexpected signing in fault fixture");
                });
            return new GoogleCloudObjectStorage(client.Object, UrlSigner.FromBlobSigner(signer.Object), journal);
        }

        private static GoogleApiException ApiError(HttpStatusCode status) => new("storage", "controlled provider rejection") { HttpStatusCode = status };

        private async Task<StorageObject> UploadControlledAsync(StorageObject item, Stream content, UploadObjectOptions options, CancellationToken token)
        {
            UploadCalls++;
            if (BeforeUpload is not null) await BeforeUpload(item);
            Assert.Equal(0, options.IfGenerationMatch);
            if (DiscardByteCapture) await content.CopyToAsync(Stream.Null, token);
            else
            {
                using var bytes = new MemoryStream();
                await content.CopyToAsync(bytes, token);
                UploadedBytes.Add(item.Name, bytes.ToArray());
            }
            Objects.Add(item.Name, 17);
            if (LoseUploadAcknowledgment)
            {
                CancelAtOutcome?.Cancel();
                throw new IOException("controlled upload acknowledgment lost");
            }
            return new StorageObject { Generation = 17 };
        }
    }

    private sealed class FaultOnceAcquireStore(IUploadIdempotencyStore inner, bool failOnce) : IUploadIdempotencyStore
    {
        public Task<UploadAcquireResult> AcquireAsync(string identity, string fingerprint, string path, CancellationToken token)
        {
            if (failOnce) { failOnce = false; throw new IOException("controlled pre-acquire transport failure"); }
            return inner.AcquireAsync(identity, fingerprint, path, token);
        }
        public Task<bool> RenewAsync(string identity, string reservation, CancellationToken token) => inner.RenewAsync(identity, reservation, token);
        public Task CompleteAsync(string identity, string fingerprint, string reservation, UploadResultResponse response, CancellationToken token) =>
            inner.CompleteAsync(identity, fingerprint, reservation, response, token);
        public Task ReleaseAsync(string identity, string reservation, CancellationToken token) => inner.ReleaseAsync(identity, reservation, token);
        public Task MarkUnknownAsync(string identity, string reservation, UploadResultResponse? response, CancellationToken token) =>
            inner.MarkUnknownAsync(identity, reservation, response, token);
    }

    private sealed class AdversarialFormStartupFilter(IFormFile caller) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, proceed) =>
            {
                if (context.Request.Path == "/Uploads" && context.Request.Method == "POST")
                {
                    var parsed = await context.Request.ReadFormAsync(context.RequestAborted);
                    context.Request.Form = new FormCollection(parsed.ToDictionary(item => item.Key, item => item.Value),
                        new FormFileCollection { caller });
                }
                await proceed(context);
            });
            next(app);
        };
    }

    private sealed class RecoveryFactory(string connectionString, ControlledCloud cloud,
        IConnectionMultiplexer? redis = null, IFormFile? adversarialFile = null, ControlledScanner? observedScanner = null,
        IUploadIdempotencyStore? checkpointStore = null) : WebApplicationFactory<Program>
    {
        private readonly RSA signingKey = RSA.Create(2048);
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("ConnectionStrings:FileDbContext", connectionString);
            builder.UseSetting("Cache:RedisEnabled", "false");
            builder.UseSetting("Jwt:PublicKey", Convert.ToBase64String(Encoding.UTF8.GetBytes(signingKey.ExportSubjectPublicKeyInfoPem())));
            builder.UseSetting("Jwt:Issuer", "https://issuer.example.invalid");
            builder.UseSetting("Jwt:Audience", "https://file.example.invalid");
            builder.UseSetting("FileStorage:Enabled", "true");
            builder.UseSetting("FileStorage:WritesEnabled", "true");
            builder.UseSetting("FileStorage:AllowedBuckets:0", "private");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IObjectStorage>();
                services.AddScoped<IObjectStorage>(provider => cloud.Storage(provider.GetRequiredService<IStorageMoveJournal>()));
                services.RemoveAll<IFileSafetyScanner>();
                services.AddScoped<IFileSafetyScanner>(_ => observedScanner ?? new ControlledScanner());
                services.RemoveAll<IUploadIdempotencyStore>();
                // Unkeyed HTTP calls never use this external checkpoint transport; strict failure prevents accidental success.
                if (checkpointStore is not null) services.AddSingleton(checkpointStore);
                else if (redis is null) services.AddSingleton(new Mock<IUploadIdempotencyStore>(MockBehavior.Strict).Object);
                else services.AddSingleton<IUploadIdempotencyStore>(new RedisUploadIdempotencyStore(redis));
                if (adversarialFile is not null) services.AddSingleton<IStartupFilter>(new AdversarialFormStartupFilter(adversarialFile));
            });
        }

        public string Token(string identity)
        {
            using var wrongKey = identity == "wrong-key" ? RSA.Create(2048) : null;
            var claims = new List<Claim> { new(JwtRegisteredClaimNames.Sub, "recovery-fixture") };
            if (identity is "create" or "wrong-key") claims.Add(new Claim("permissions", FilePermissions.Create));
            if (identity == "update") claims.Add(new Claim("permissions", FilePermissions.Update));
            var now = DateTime.UtcNow;
            return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("https://issuer.example.invalid", "https://file.example.invalid",
                claims, now.AddMinutes(-1), now.AddMinutes(5), new SigningCredentials(new RsaSecurityKey(wrongKey ?? signingKey), SecurityAlgorithms.RsaSha256)));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) signingKey.Dispose();
        }
    }
}
