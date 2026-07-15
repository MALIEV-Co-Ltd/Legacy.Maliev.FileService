using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Legacy.Maliev.FileService.Data;

/// <summary>Creates the context for explicit design-time migration commands.</summary>
public sealed class FileDbContextFactory : IDesignTimeDbContextFactory<FileDbContext>
{
    /// <inheritdoc />
    public FileDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__FileDbContext");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings__FileDbContext is required for design-time migration commands.");
        }

        var options = new DbContextOptionsBuilder<FileDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new FileDbContext(options);
    }
}
