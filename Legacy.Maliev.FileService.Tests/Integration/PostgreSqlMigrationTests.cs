using Legacy.Maliev.FileService.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
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
    private const string InitialMigration = "20260715033302_InitialPostgresCompatibility";
    private const string InstantQuoteMigration = "20260719033405_AddInstantQuoteUploadWorkflow";

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
    public void InstantQuoteMigration_UnshippedSquash_HasOneAdditiveFinalSchemaMigration()
    {
        using var context = fixture.CreateContext();
        var migrations = context.GetService<IMigrationsAssembly>().Migrations.Keys.ToArray();

        Assert.Equal([InitialMigration, InstantQuoteMigration], migrations);
    }

    [Fact]
    public void InstantQuoteMigration_UpOperations_AreAdditiveWithoutDataTransformsOrCustomXmin()
    {
        using var context = fixture.CreateContext();
        var assembly = context.GetService<IMigrationsAssembly>();
        var migrationType = assembly.Migrations[InstantQuoteMigration];
        var migration = assembly.CreateMigration(migrationType, context.Database.ProviderName!);

        Assert.DoesNotContain(migration.UpOperations, operation => operation is DropColumnOperation
            or DropTableOperation
            or AlterColumnOperation
            or SqlOperation);

        var createdColumns = migration.UpOperations
            .OfType<CreateTableOperation>()
            .SelectMany(operation => operation.Columns)
            .ToArray();
        Assert.DoesNotContain(createdColumns, column => string.Equals(column.Name, "xmin", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task InstantQuoteMigration_RollbackThenUp_RecreatesFinalSchemaWithoutPendingChanges()
    {
        await using var context = fixture.CreateContext();
        await ResetSchemaAsync(context);
        try
        {
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync();
            await migrator.MigrateAsync(InitialMigration);
            Assert.False(await TableExistsAsync(context, "InstantQuoteUploadSession"));

            await migrator.MigrateAsync();

            Assert.True(await TableExistsAsync(context, "InstantQuoteUploadSession"));
            Assert.True(await TableExistsAsync(context, "InstantQuoteUploadFile"));
            Assert.True(await TableExistsAsync(context, "InstantQuoteFinalization"));
            Assert.False(context.Database.HasPendingModelChanges());
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
            await ResetSchemaAsync(context);
        }
    }

    private static async Task<bool> TableExistsAsync(FileDbContext context, string tableName)
    {
        await context.Database.OpenConnectionAsync();
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT to_regclass(@table_name) IS NOT NULL;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "table_name";
        parameter.Value = $"public.\"{tableName}\"";
        command.Parameters.Add(parameter);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static Task ResetSchemaAsync(FileDbContext context) =>
        context.Database.ExecuteSqlRawAsync("DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;");
}
