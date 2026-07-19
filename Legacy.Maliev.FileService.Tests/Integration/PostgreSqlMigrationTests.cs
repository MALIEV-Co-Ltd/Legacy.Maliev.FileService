using Legacy.Maliev.FileService.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Legacy.Maliev.FileService.Tests.Integration;

[CollectionDefinition(Name)]
public sealed class PostgreSqlCollection : ICollectionFixture<PostgreSqlFixture>
{
    public const string Name = "FilePostgreSQL";
}

public sealed class PostgreSqlFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public Task InitializeAsync() => container.StartAsync();

    public async Task DisposeAsync() => await container.DisposeAsync();

    public FileDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<FileDbContext>().UseNpgsql(container.GetConnectionString()).Options;
        return new FileDbContext(options);
    }
}

[Collection(PostgreSqlCollection.Name)]
public sealed class PostgreSqlMigrationTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task InitialMigration_FreshPostgreSql_CreatesLegacyUploadTableWithoutCustomXmin()
    {
        await using var context = fixture.CreateContext();

        await context.Database.MigrateAsync();

        await context.Database.OpenConnectionAsync();
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name = 'Upload'
              AND column_name IN ('ID', 'Bucket', 'ContentType', 'Name', 'Size', 'CreatedDate', 'ModifiedDate');
            """;
        var columnCount = Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(7, columnCount);

        command.CommandText = "SELECT xmin FROM \"Upload\" LIMIT 0;";
        await command.ExecuteNonQueryAsync();

        command.CommandText = """
            SELECT COUNT(*)
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND ((table_name = 'InstantQuoteFinalization' AND column_name = 'QuotationRequestId')
                OR (table_name = 'InstantQuoteUploadFile' AND column_name = 'FinalizedQuotationRequestId'))
              AND data_type = 'integer';
            """;
        var integerAuthorityColumns = Convert.ToInt32(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(2, integerAuthorityColumns);
    }

    [Fact]
    public async Task ObjectAuthorityMigration_PreExistingWorkflowRow_FailsClosedBeforeAddingColumns()
    {
        await using var context = fixture.CreateContext();
        await ResetSchemaAsync(context);
        try
        {
            await context.GetService<IMigrator>().MigrateAsync("20260718135201_AddInstantQuoteUploadWorkflow");
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "InstantQuoteUploadSession"
                    ("Id", "OwnerSubject", "IsAuthenticated", "TokenHash", "ExpiresAt", "CreatedAt")
                VALUES
                    ('11111111-1111-1111-1111-111111111111', 'owner', TRUE,
                     decode(repeat('01', 32), 'hex'), '2026-07-20T00:00:00Z', '2026-07-18T00:00:00Z');

                INSERT INTO "InstantQuoteUploadFile"
                    ("Id", "SessionId", "IdempotencyKeyHash", "RequestFingerprint", "OriginalFileName",
                     "ValidatedExtension", "ValidatedContentType", "ExpectedSha256", "TemporaryObjectName",
                     "State", "CreatedAt", "ModifiedAt")
                VALUES
                    ('22222222-2222-2222-2222-222222222222', '11111111-1111-1111-1111-111111111111',
                     decode(repeat('02', 32), 'hex'), repeat('a', 64), 'part.stl', '.stl', 'model/stl',
                     repeat('b', 64), 'instant-quotation/temp/part.stl', 'Pending',
                     '2026-07-18T00:00:00Z', '2026-07-18T00:00:00Z');
                """);

            var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                context.GetService<IMigrator>().MigrateAsync());

            Assert.Contains("must be empty", exception.MessageText, StringComparison.Ordinal);
            await context.Database.OpenConnectionAsync();
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = """
                SELECT COUNT(*) FROM information_schema.columns
                WHERE table_name = 'InstantQuoteUploadFile'
                  AND column_name IN ('TemporaryBucket', 'FinalBucket', 'FinalizedQuotationRequestId');
                """;
            Assert.Equal(0, Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
            await context.Database.CloseConnectionAsync();
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
            await ResetSchemaAsync(context);
        }
    }

    [Fact]
    public async Task ObjectAuthorityMigration_ConcurrentLegacyWriter_BlocksThenFailsClosed()
    {
        await using var migrationContext = fixture.CreateContext();
        await ResetSchemaAsync(migrationContext);
        try
        {
            await migrationContext.GetService<IMigrator>().MigrateAsync("20260718135201_AddInstantQuoteUploadWorkflow");
            await using var writerContext = fixture.CreateContext();
            await using var writerTransaction = await writerContext.Database.BeginTransactionAsync();
            await writerContext.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "InstantQuoteUploadSession"
                    ("Id", "OwnerSubject", "IsAuthenticated", "TokenHash", "ExpiresAt", "CreatedAt")
                VALUES
                    ('11111111-1111-1111-1111-111111111111', 'owner', TRUE,
                     decode(repeat('01', 32), 'hex'), '2026-07-20T00:00:00Z', '2026-07-18T00:00:00Z');

                INSERT INTO "InstantQuoteUploadFile"
                    ("Id", "SessionId", "IdempotencyKeyHash", "RequestFingerprint", "OriginalFileName",
                     "ValidatedExtension", "ValidatedContentType", "ExpectedSha256", "TemporaryObjectName",
                     "State", "CreatedAt", "ModifiedAt")
                VALUES
                    ('22222222-2222-2222-2222-222222222222', '11111111-1111-1111-1111-111111111111',
                     decode(repeat('02', 32), 'hex'), repeat('a', 64), 'part.stl', '.stl', 'model/stl',
                     repeat('b', 64), 'instant-quotation/temp/part.stl', 'Pending',
                     '2026-07-18T00:00:00Z', '2026-07-18T00:00:00Z');
                """);

            var migration = migrationContext.GetService<IMigrator>().MigrateAsync();
            await Assert.ThrowsAsync<TimeoutException>(() => migration.WaitAsync(TimeSpan.FromMilliseconds(250)));
            await writerTransaction.CommitAsync();

            var exception = await Assert.ThrowsAsync<PostgresException>(() => migration);
            Assert.Contains("must be empty", exception.MessageText, StringComparison.Ordinal);
        }
        finally
        {
            await migrationContext.Database.CloseConnectionAsync();
            await ResetSchemaAsync(migrationContext);
        }
    }

    [Fact]
    public async Task IntegerQuotationAuthorityMigration_PreExistingFinalization_FailsClosedWithoutTypeConversion()
    {
        await using var context = fixture.CreateContext();
        await ResetSchemaAsync(context);
        try
        {
            await context.GetService<IMigrator>().MigrateAsync("20260718162404_AddInstantQuoteObjectBuckets");
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "InstantQuoteUploadSession"
                    ("Id", "OwnerSubject", "IsAuthenticated", "TokenHash", "ExpiresAt", "CreatedAt")
                VALUES
                    ('11111111-1111-1111-1111-111111111111', 'owner', TRUE,
                     decode(repeat('01', 32), 'hex'), '2026-07-20T00:00:00Z', '2026-07-18T00:00:00Z');

                INSERT INTO "InstantQuoteFinalization"
                    ("Id", "SessionId", "IdempotencyKeyHash", "RequestFingerprint", "QuotationRequestId",
                     "SelectedFileIds", "State", "CreatedAt", "ModifiedAt")
                VALUES
                    ('22222222-2222-2222-2222-222222222222', '11111111-1111-1111-1111-111111111111',
                     decode(repeat('02', 32), 'hex'), repeat('a', 64),
                     '33333333-3333-3333-3333-333333333333', '{{}}'::uuid[], 'Pending',
                     '2026-07-18T00:00:00Z', '2026-07-18T00:00:00Z');
                """);

            var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                context.GetService<IMigrator>().MigrateAsync());

            Assert.Contains("must be empty", exception.MessageText, StringComparison.Ordinal);
            await context.Database.OpenConnectionAsync();
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = """
                SELECT data_type
                FROM information_schema.columns
                WHERE table_name = 'InstantQuoteFinalization' AND column_name = 'QuotationRequestId';
                """;
            Assert.Equal("uuid", await command.ExecuteScalarAsync());
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
            await ResetSchemaAsync(context);
        }
    }

    private static Task ResetSchemaAsync(FileDbContext context) =>
        context.Database.ExecuteSqlRawAsync("DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;");
}
