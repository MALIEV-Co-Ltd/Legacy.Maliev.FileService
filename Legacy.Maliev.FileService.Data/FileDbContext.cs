using Legacy.Maliev.FileService.Domain;
using Microsoft.EntityFrameworkCore;

namespace Legacy.Maliev.FileService.Data;

/// <summary>PostgreSQL context preserving the legacy Upload table contract.</summary>
public sealed class FileDbContext(DbContextOptions<FileDbContext> options) : DbContext(options)
{
    private const string UploadIdempotencyIndexName = "IX_InstantQuoteUploadFile_SessionId_IdempotencyKeyHash";
    private const string FinalizationIdempotencyIndexName = "IX_InstantQuoteFinalization_SessionId_IdempotencyKeyHash";

    /// <summary>Gets clean upload metadata.</summary>
    public DbSet<Upload> Uploads => Set<Upload>();

    /// <summary>Gets instant-quotation upload sessions.</summary>
    public DbSet<InstantQuoteUploadSession> InstantQuoteUploadSessions => Set<InstantQuoteUploadSession>();

    /// <summary>Gets instant-quotation upload reservations.</summary>
    public DbSet<InstantQuoteUploadFile> InstantQuoteUploadFiles => Set<InstantQuoteUploadFile>();

    /// <summary>Gets instant-quotation finalization reservations.</summary>
    public DbSet<InstantQuoteFinalization> InstantQuoteFinalizations => Set<InstantQuoteFinalization>();

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

        var session = modelBuilder.Entity<InstantQuoteUploadSession>();
        session.ToTable("InstantQuoteUploadSession", table =>
            table.HasCheckConstraint("CK_InstantQuoteUploadSession_TokenHash_Length", "octet_length(\"TokenHash\") = 32"));
        session.HasKey(value => value.Id);
        session.Property(value => value.OwnerSubject).HasMaxLength(512);
        session.Property(value => value.TokenHash).HasColumnType("bytea").IsRequired();
        session.Property(value => value.ExpiresAt).HasColumnType("timestamp with time zone");
        session.Property(value => value.CreatedAt).HasColumnType("timestamp with time zone");
        session.Property<uint>("xmin").HasColumnType("xid").IsRowVersion();

        var instantFile = modelBuilder.Entity<InstantQuoteUploadFile>();
        instantFile.ToTable("InstantQuoteUploadFile", table =>
        {
            table.HasCheckConstraint("CK_InstantQuoteUploadFile_KeyHash_Length", "octet_length(\"IdempotencyKeyHash\") = 32");
            table.HasCheckConstraint("CK_InstantQuoteUploadFile_Fingerprint", "\"RequestFingerprint\" ~ '^[0-9a-f]{64}$'");
            table.HasCheckConstraint("CK_InstantQuoteUploadFile_ExpectedSha256", "\"ExpectedSha256\" ~ '^[0-9a-f]{64}$'");
            table.HasCheckConstraint("CK_InstantQuoteUploadFile_ActualSha256", "\"ActualSha256\" IS NULL OR \"ActualSha256\" ~ '^[0-9a-f]{64}$'");
        });
        instantFile.HasKey(value => value.Id);
        instantFile.Property(value => value.IdempotencyKeyHash).HasColumnType("bytea").IsRequired();
        instantFile.Property(value => value.RequestFingerprint).HasMaxLength(64).IsFixedLength().IsRequired();
        instantFile.Property(value => value.OriginalFileName).HasMaxLength(1024).IsRequired();
        instantFile.Property(value => value.ValidatedExtension).HasMaxLength(16).IsRequired();
        instantFile.Property(value => value.ValidatedContentType).HasMaxLength(255).IsRequired();
        instantFile.Property(value => value.ExpectedSha256).HasMaxLength(64).IsFixedLength().IsRequired();
        instantFile.Property(value => value.ActualSha256).HasMaxLength(64).IsFixedLength();
        instantFile.Property(value => value.TemporaryBucket).HasMaxLength(255).IsRequired();
        instantFile.Property(value => value.TemporaryObjectName).HasMaxLength(1024).IsRequired();
        instantFile.Property(value => value.FinalBucket).HasMaxLength(255);
        instantFile.Property(value => value.FinalObjectName).HasMaxLength(1024);
        instantFile.Property(value => value.State).HasConversion<string>().HasMaxLength(16).IsRequired();
        instantFile.Property(value => value.CreatedAt).HasColumnType("timestamp with time zone");
        instantFile.Property(value => value.ModifiedAt).HasColumnType("timestamp with time zone");
        instantFile.Property<uint>("xmin").HasColumnType("xid").IsRowVersion();
        instantFile.HasIndex(value => new { value.SessionId, value.IdempotencyKeyHash })
            .IsUnique()
            .HasDatabaseName(UploadIdempotencyIndexName);
        instantFile.HasOne<InstantQuoteUploadSession>().WithMany().HasForeignKey(value => value.SessionId).OnDelete(DeleteBehavior.Cascade);

        var finalization = modelBuilder.Entity<InstantQuoteFinalization>();
        finalization.ToTable("InstantQuoteFinalization", table =>
        {
            table.HasCheckConstraint("CK_InstantQuoteFinalization_KeyHash_Length", "octet_length(\"IdempotencyKeyHash\") = 32");
            table.HasCheckConstraint("CK_InstantQuoteFinalization_Fingerprint", "\"RequestFingerprint\" ~ '^[0-9a-f]{64}$'");
        });
        finalization.HasKey(value => value.Id);
        finalization.Property(value => value.IdempotencyKeyHash).HasColumnType("bytea").IsRequired();
        finalization.Property(value => value.RequestFingerprint).HasMaxLength(64).IsFixedLength().IsRequired();
        finalization.Property(value => value.SelectedFileIds).HasColumnType("uuid[]").IsRequired();
        finalization.Property(value => value.State).HasConversion<string>().HasMaxLength(16).IsRequired();
        finalization.Property(value => value.CreatedAt).HasColumnType("timestamp with time zone");
        finalization.Property(value => value.ModifiedAt).HasColumnType("timestamp with time zone");
        finalization.Property<uint>("xmin").HasColumnType("xid").IsRowVersion();
        finalization.HasIndex(value => new { value.SessionId, value.IdempotencyKeyHash })
            .IsUnique()
            .HasDatabaseName(FinalizationIdempotencyIndexName);
        finalization.HasOne<InstantQuoteUploadSession>().WithMany().HasForeignKey(value => value.SessionId).OnDelete(DeleteBehavior.Cascade);
    }
}
