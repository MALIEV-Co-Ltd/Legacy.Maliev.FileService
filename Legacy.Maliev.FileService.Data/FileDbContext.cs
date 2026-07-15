using Legacy.Maliev.FileService.Domain;
using Microsoft.EntityFrameworkCore;

namespace Legacy.Maliev.FileService.Data;

/// <summary>PostgreSQL context preserving the legacy Upload table contract.</summary>
public sealed class FileDbContext(DbContextOptions<FileDbContext> options) : DbContext(options)
{
    /// <summary>Gets clean upload metadata.</summary>
    public DbSet<Upload> Uploads => Set<Upload>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var upload = modelBuilder.Entity<Upload>();
        upload.ToTable("Upload");
        upload.HasKey(value => value.Id);
        upload.Property(value => value.Id).HasColumnName("ID").ValueGeneratedOnAdd();
        upload.Property(value => value.Bucket).HasMaxLength(50).IsRequired();
        upload.Property(value => value.ContentType).HasMaxLength(50).IsRequired();
        upload.Property(value => value.Name).IsRequired();
        upload.Property(value => value.Size).IsRequired();
        upload.Property(value => value.CreatedDate)
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP");
        upload.Property(value => value.ModifiedDate)
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP");
        upload.HasIndex(value => new { value.Bucket, value.Name })
            .IsUnique()
            .HasDatabaseName("IX_Upload_Bucket_Name");
    }
}
