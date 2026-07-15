using Legacy.Maliev.FileService.Data;
using Microsoft.EntityFrameworkCore;
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
    }
}
