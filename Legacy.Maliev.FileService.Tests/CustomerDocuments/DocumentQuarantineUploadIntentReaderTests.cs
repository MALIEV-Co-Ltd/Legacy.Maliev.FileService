using System.Data.Common;
using Legacy.Maliev.FileService.Application.CustomerDocuments;
using Legacy.Maliev.FileService.Data;
using Legacy.Maliev.FileService.Data.CustomerDocuments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Legacy.Maliev.FileService.Tests.CustomerDocuments;

public sealed class DocumentQuarantineUploadIntentPostgreSqlFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:18-alpine").Build();
    public async Task InitializeAsync()
    {
        await container.StartAsync();
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }
    public async Task DisposeAsync() => await container.DisposeAsync();
    public FileDbContext CreateContext() => new(new DbContextOptionsBuilder<FileDbContext>().UseNpgsql(container.GetConnectionString()).Options);
}

// Uses the actual existing File migration/writer/DbSet in an isolated synthetic database; no production acceptance is asserted.
public sealed class DocumentQuarantineUploadIntentReaderTests(DocumentQuarantineUploadIntentPostgreSqlFixture fixture)
    : IClassFixture<DocumentQuarantineUploadIntentPostgreSqlFixture>
{
    [Theory]
    [InlineData("Pending", null)]
    [InlineData("Uploaded", 73L)]
    [InlineData("Unknown", null)]
    [InlineData("Unknown", 73L)]
    public async Task ExistingOwnerWriterCustodyIsExactReadonlyAndNeverClean(string state, long? generation)
    {
        var request = Request();
        await using var db = fixture.CreateContext();
        await SeedThroughOwnerAsync(db, request, state, generation);
        var before = await db.QuarantineUploadIntents.AsNoTracking().SingleAsync(x => x.OperationId == request.Reservation.OperationId);
        var snapshot = await Reader(db).ReadAsync(request, default);
        Assert.Equal(before.OperationId, snapshot.OperationId);
        Assert.Equal(before.ParentOperationId, snapshot.ParentOperationId);
        Assert.Equal(before.Bucket, snapshot.Bucket);
        Assert.Equal(before.ObjectName, snapshot.ObjectName);
        Assert.Equal(before.ContentType, snapshot.ContentType);
        Assert.Equal(before.DeclaredSize, snapshot.DeclaredSize);
        Assert.Equal(generation, snapshot.AcknowledgedGeneration);
        Assert.Equal(state, snapshot.State);
        Assert.False(snapshot.IsCleanEvidence);
        Assert.Equal(TimeSpan.Zero, snapshot.CreatedAtUtc.Offset);
        Assert.Equal(TimeSpan.Zero, snapshot.ModifiedAtUtc.Offset);
        Assert.True(snapshot.ModifiedAtUtc >= snapshot.CreatedAtUtc);
        Assert.Empty(db.ChangeTracker.Entries());
        var after = await db.QuarantineUploadIntents.AsNoTracking().SingleAsync(x => x.OperationId == snapshot.OperationId);
        Assert.Equal(before.ModifiedAt, after.ModifiedAt);
        Assert.Equal(before.State, after.State);
        Assert.Empty(await db.StorageMoveJournals.Where(x => x.OperationId == snapshot.OperationId).ToListAsync());
        Assert.Empty(await db.Uploads.Where(x => x.Bucket == snapshot.Bucket && x.Name == snapshot.ObjectName).ToListAsync());
    }

    [Fact]
    public async Task MissingExactOperationDoesNotGuessByMatchingCoordinates()
    {
        var request = Request();
        await using var db = fixture.CreateContext();
        var wrongOperation = Guid.NewGuid();
        await new QuarantineUploadIntentRepository(db, TimeProvider.System).PrepareAsync(wrongOperation, wrongOperation,
            request.ExpectedBucket, ObjectName(request.Reservation), request.ExpectedContentType, request.ExpectedDeclaredSize, default);
        await Assert.ThrowsAsync<DocumentAuthorityUnavailableException>(() => Reader(db).ReadAsync(request, default));
    }

    [Theory]
    [InlineData("parent")]
    [InlineData("bucket")]
    [InlineData("customer")]
    [InlineData("document")]
    [InlineData("version")]
    [InlineData("operationPath")]
    [InlineData("filenameGuess")]
    [InlineData("type")]
    [InlineData("size")]
    [InlineData("pendingAcknowledged")]
    [InlineData("uploadedMissingGeneration")]
    [InlineData("state")]
    [InlineData("created")]
    [InlineData("timeOrder")]
    public async Task InvalidStoredCustodyRefusesBeforeReturningPrivateSnapshot(string fault)
    {
        var request = Request();
        await using var db = fixture.CreateContext();
        await SeedThroughOwnerAsync(db, request, "Pending", null);
        var row = await db.QuarantineUploadIntents.SingleAsync(x => x.OperationId == request.Reservation.OperationId);
        switch (fault)
        {
            case "parent": row.ParentOperationId = Guid.NewGuid(); break;
            case "bucket": row.Bucket = "other-private"; break;
            case "customer": row.ObjectName = ObjectName(request.Reservation with { CustomerId = 24 }); break;
            case "document": row.ObjectName = ObjectName(request.Reservation with { DocumentId = Guid.NewGuid() }); break;
            case "version": row.ObjectName = ObjectName(request.Reservation with { VersionId = Guid.NewGuid() }); break;
            case "operationPath": row.ObjectName = ObjectName(request.Reservation with { OperationId = Guid.NewGuid() }); break;
            case "filenameGuess": row.ObjectName = "customer-documents/23/synthetic.pdf"; break;
            case "type": row.ContentType = "text/plain"; break;
            case "size": row.DeclaredSize++; break;
            case "pendingAcknowledged": row.AcknowledgedGeneration = 73; break;
            case "uploadedMissingGeneration": row.State = "Uploaded"; break;
            case "state": row.State = "ScanClean"; break;
            case "created": row.CreatedAt = DateTimeOffset.MinValue; break;
            case "timeOrder": row.ModifiedAt = row.CreatedAt.AddSeconds(-1); break;
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<DocumentAuthorityUnavailableException>(() => Reader(db).ReadAsync(request, default));
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("document")]
    [InlineData("version")]
    [InlineData("customer")]
    [InlineData("bucket")]
    [InlineData("type")]
    [InlineData("size")]
    public async Task InvalidServerReservationCannotSelectAnIntent(string fault)
    {
        var request = Request();
        request = fault switch
        {
            "operation" => request with { Reservation = request.Reservation with { OperationId = Guid.Empty } },
            "document" => request with { Reservation = request.Reservation with { DocumentId = Guid.Empty } },
            "version" => request with { Reservation = request.Reservation with { VersionId = Guid.Empty } },
            "customer" => request with { Reservation = request.Reservation with { CustomerId = 0 } },
            "bucket" => request with { ExpectedBucket = "" },
            "type" => request with { ExpectedContentType = "text/plain" },
            _ => request with { ExpectedDeclaredSize = 0 },
        };
        await using var db = fixture.CreateContext();
        await Assert.ThrowsAsync<DocumentAuthorityUnavailableException>(() => Reader(db).ReadAsync(request, default));
    }

    [Fact]
    public async Task PhysicallyIncompleteSchemaCannotBeTreatedAsIntentAuthority()
    {
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"QuarantineUploadIntent\" DROP CONSTRAINT \"CK_QuarantineUploadIntent_Generation\"");
        await Assert.ThrowsAsync<DocumentAuthorityUnavailableException>(() => Reader(db).ReadAsync(Request(), default));
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task DependencyUnavailableIsSanitized()
    {
        await using var db = fixture.CreateContext();
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("SET search_path TO synthetic_absent_schema");
        await Assert.ThrowsAsync<DocumentAuthorityUnavailableException>(() => Reader(db).ReadAsync(Request(), default));
    }

    [Fact]
    public async Task CallerCancellationPropagates()
    {
        await using var db = fixture.CreateContext();
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Reader(db).ReadAsync(Request(), caller.Token));
    }

    [Fact]
    public async Task DisabledReaderIsUnavailable()
    {
        await using var db = fixture.CreateContext();
        await Assert.ThrowsAsync<DocumentAuthorityUnavailableException>(() => new DocumentQuarantineUploadIntentReader(db).ReadAsync(Request(), default));
    }

    [Fact]
    public async Task MissingReaderDependencyIsUnavailable()
    {
        await Assert.ThrowsAsync<DocumentAuthorityUnavailableException>(() => new DocumentQuarantineUploadIntentReader(enabled: true).ReadAsync(Request(), default));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task InvalidReservationFingerprintCannotReadOtherwiseMatchingCustody(string? fingerprint)
    {
        var request = Request();
        await using var db = fixture.CreateContext();
        await SeedThroughOwnerAsync(db, request, "Uploaded", 73);
        var invalid = request with { Reservation = request.Reservation with { Fingerprint = fingerprint! } };
        await Assert.ThrowsAsync<DocumentAuthorityUnavailableException>(() => Reader(db).ReadAsync(invalid, default));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task NonpositiveReservedVersionNumberCannotReadOtherwiseMatchingCustody(int versionNumber)
    {
        var request = Request();
        await using var db = fixture.CreateContext();
        await SeedThroughOwnerAsync(db, request, "Uploaded", 73);
        var invalid = request with { Reservation = request.Reservation with { VersionNumber = versionNumber } };
        await Assert.ThrowsAsync<DocumentAuthorityUnavailableException>(() => Reader(db).ReadAsync(invalid, default));
    }

    [Fact]
    public async Task UnconfiguredDatabaseProviderCannotBecomeCustodyAuthority()
    {
        await using var db = new FileDbContext(new DbContextOptionsBuilder<FileDbContext>().Options);
        await Assert.ThrowsAsync<DocumentAuthorityUnavailableException>(() => Reader(db).ReadAsync(Request(), default));
    }

    [Theory]
    [InlineData("modifiedDefault")]
    [InlineData("createdInfinite")]
    [InlineData("modifiedInfinite")]
    [InlineData("createdFuture")]
    [InlineData("modifiedFuture")]
    public async Task InvalidFiniteOrCurrentUtcTimesCannotReadOtherwiseMatchingCustody(string fault)
    {
        var request = Request();
        await using var db = fixture.CreateContext();
        await SeedThroughOwnerAsync(db, request, "Uploaded", 73);
        var row = await db.QuarantineUploadIntents.SingleAsync(x => x.OperationId == request.Reservation.OperationId);
        switch (fault)
        {
            case "modifiedDefault": row.ModifiedAt = default; break;
            case "createdInfinite": row.CreatedAt = DateTimeOffset.MaxValue; row.ModifiedAt = row.CreatedAt; break;
            case "modifiedInfinite": row.ModifiedAt = DateTimeOffset.MaxValue; break;
            case "createdFuture": row.CreatedAt = DateTimeOffset.UtcNow.AddDays(1); row.ModifiedAt = row.CreatedAt; break;
            case "modifiedFuture": row.ModifiedAt = DateTimeOffset.UtcNow.AddDays(1); break;
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<DocumentAuthorityUnavailableException>(() => Reader(db).ReadAsync(request, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecognizedProviderFailurePreservesConcurrentCallerCancellation(bool cancelCaller)
    {
        using var caller = new CancellationTokenSource();
        var interceptor = new RejectConnectionOpening(caller, cancelCaller);
        await using var db = new FileDbContext(new DbContextOptionsBuilder<FileDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=synthetic_never_opened;Username=synthetic")
            .AddInterceptors(interceptor).Options);

        if (cancelCaller)
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Reader(db).ReadAsync(Request(), caller.Token));
            Assert.Equal(caller.Token, error.CancellationToken);
        }
        else
        {
            await Assert.ThrowsAsync<DocumentAuthorityUnavailableException>(() => Reader(db).ReadAsync(Request(), caller.Token));
            Assert.False(caller.IsCancellationRequested);
        }
        Assert.Equal(1, interceptor.Attempts);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData("default")]
    [InlineData("maximum")]
    [InlineData("nonUtc")]
    [InlineData("beforeModified")]
    public async Task InvalidControlledClockRefusesOtherwiseMatchingOwnerCustody(string fault)
    {
        var request = Request();
        await using var db = fixture.CreateContext();
        await SeedThroughOwnerAsync(db, request, "Uploaded", 73);
        var row = await db.QuarantineUploadIntents.AsNoTracking().SingleAsync(x => x.OperationId == request.Reservation.OperationId);
        var now = fault switch
        {
            "default" => default,
            "maximum" => DateTimeOffset.MaxValue,
            "nonUtc" => row.ModifiedAt.AddMinutes(1).ToOffset(TimeSpan.FromHours(7)),
            _ => row.ModifiedAt.AddTicks(-1),
        };
        var reader = new DocumentQuarantineUploadIntentReader(db, enabled: true, timeProvider: new FixedClock(now));

        await Assert.ThrowsAsync<DocumentAuthorityUnavailableException>(() => reader.ReadAsync(request, default));
        Assert.Empty(db.ChangeTracker.Entries());
        var retained = await db.QuarantineUploadIntents.AsNoTracking().SingleAsync(x => x.OperationId == row.OperationId);
        Assert.Equal(row.ModifiedAt, retained.ModifiedAt);
        Assert.Equal(row.State, retained.State);
        Assert.Equal(row.AcknowledgedGeneration, retained.AcknowledgedGeneration);
    }

    [Fact]
    public async Task ValidControlledUtcClockAtModifiedTimeReadsExactCustodyWithoutCertifyingCleanBytes()
    {
        var request = Request();
        await using var db = fixture.CreateContext();
        await SeedThroughOwnerAsync(db, request, "Uploaded", 73);
        var row = await db.QuarantineUploadIntents.AsNoTracking().SingleAsync(x => x.OperationId == request.Reservation.OperationId);
        var reader = new DocumentQuarantineUploadIntentReader(db, enabled: true, timeProvider: new FixedClock(row.ModifiedAt));

        var snapshot = await reader.ReadAsync(request, default);
        Assert.Equal(row.OperationId, snapshot.OperationId);
        Assert.Equal(row.ParentOperationId, snapshot.ParentOperationId);
        Assert.Equal(row.Bucket, snapshot.Bucket);
        Assert.Equal(row.ObjectName, snapshot.ObjectName);
        Assert.Equal(row.ContentType, snapshot.ContentType);
        Assert.Equal(row.DeclaredSize, snapshot.DeclaredSize);
        Assert.Equal(row.AcknowledgedGeneration, snapshot.AcknowledgedGeneration);
        Assert.Equal(row.State, snapshot.State);
        Assert.Equal(row.CreatedAt, snapshot.CreatedAtUtc);
        Assert.Equal(row.ModifiedAt, snapshot.ModifiedAtUtc);
        Assert.False(snapshot.IsCleanEvidence);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // The actual EF opening boundary runs before provider network/database access, then raises a recognized provider error.
    private sealed class RejectConnectionOpening(CancellationTokenSource caller, bool cancelCaller) : DbConnectionInterceptor
    {
        public int Attempts { get; private set; }
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection,
            ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            Attempts++;
            if (cancelCaller) caller.Cancel();
            throw new NpgsqlException("Synthetic intercepted provider failure; no connection opened.");
        }
    }

    private static DocumentQuarantineUploadIntentReader Reader(FileDbContext db) => new(db, enabled: true);
    private static DocumentQuarantineUploadIntentRequest Request() => new(new DocumentUploadReservation(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 23, 1, new string('a', 64)), "synthetic-private", "application/pdf", 17);
    private static string ObjectName(DocumentUploadReservation reservation) =>
        $"customer-documents/{reservation.CustomerId}/{reservation.DocumentId:N}/{reservation.VersionId:N}/quarantine/{reservation.OperationId:N}";
    private static async Task SeedThroughOwnerAsync(FileDbContext db, DocumentQuarantineUploadIntentRequest request, string state, long? generation)
    {
        var writer = new QuarantineUploadIntentRepository(db, TimeProvider.System);
        await writer.PrepareAsync(request.Reservation.OperationId, request.Reservation.OperationId, request.ExpectedBucket,
            ObjectName(request.Reservation), request.ExpectedContentType, request.ExpectedDeclaredSize, default);
        if (generation is not null) await writer.AcknowledgeAsync(request.Reservation.OperationId, generation.Value, default);
        if (state == "Unknown") await writer.UnknownAsync(request.Reservation.OperationId, default);
    }
}
