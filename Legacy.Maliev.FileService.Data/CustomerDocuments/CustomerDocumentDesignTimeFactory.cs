using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
namespace Legacy.Maliev.FileService.Data.CustomerDocuments;
/// <summary>Builds the isolated registry model for offline migration generation.</summary>
public sealed class CustomerDocumentDesignTimeFactory : IDesignTimeDbContextFactory<CustomerDocumentDbContext>
{
    /// <inheritdoc />
    public CustomerDocumentDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<CustomerDocumentDbContext>().UseNpgsql(
            x => x.MigrationsHistoryTable("__CustomerDocumentMigrationsHistory")).Options);
}
