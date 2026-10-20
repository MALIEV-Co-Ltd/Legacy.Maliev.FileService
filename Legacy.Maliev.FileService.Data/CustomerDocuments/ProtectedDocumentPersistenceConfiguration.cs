using Legacy.Maliev.FileService.Domain.CustomerDocuments;
using Microsoft.EntityFrameworkCore;
namespace Legacy.Maliev.FileService.Data.CustomerDocuments;
/// <summary>Configures additive durable upload reservations and retained legal holds.</summary>
public static class ProtectedDocumentPersistenceConfiguration
{
    /// <summary>Adds the protected workflow tables to the registry model.</summary>
    public static void Configure(ModelBuilder model)
    {
        var upload = model.Entity<ProtectedDocumentUploadCheckpoint>();
        upload.ToTable("CustomerDocumentUploadCheckpoints", table =>
        {
            table.HasCheckConstraint("CK_DocumentUpload_Scope", "\"CustomerId\" > 0 AND \"VersionNumber\" > 0");
            table.HasCheckConstraint("CK_DocumentUpload_State", "\"State\" IN ('Pending','Unknown','Completed')");
            table.HasCheckConstraint("CK_DocumentUpload_Completed", "(\"State\" = 'Completed' AND \"CompletedRevision\" IS NOT NULL AND \"CompletedRevision\" > 0) OR (\"State\" <> 'Completed' AND \"CompletedRevision\" IS NULL)");
        });
        upload.HasKey(x => x.OperationId);
        upload.Property(x => x.Subject).HasMaxLength(256).IsRequired();
        upload.Property(x => x.KeyHash).HasMaxLength(64).IsRequired();
        upload.Property(x => x.Fingerprint).HasMaxLength(64).IsRequired();
        upload.Property(x => x.State).HasMaxLength(16).IsRequired();
        upload.HasIndex(x => new { x.CustomerId, x.Subject, x.KeyHash }).IsUnique();
        upload.HasIndex(x => new { x.DocumentId, x.VersionNumber }).IsUnique();
        upload.HasIndex(x => x.VersionId).IsUnique();
        upload.HasOne<CustomerDocument>().WithMany().HasForeignKey(x => new { x.DocumentId, x.CustomerId }).HasPrincipalKey(x => new { x.Id, x.CustomerId }).OnDelete(DeleteBehavior.Restrict);
        var hold = model.Entity<CustomerDocumentLegalHold>();
        hold.ToTable("CustomerDocumentLegalHolds");
        hold.HasKey(x => x.DocumentId);
        hold.Property(x => x.Reason).HasMaxLength(1024).IsRequired();
        hold.HasOne<CustomerDocument>().WithMany().HasForeignKey(x => new { x.DocumentId, x.CustomerId }).HasPrincipalKey(x => new { x.Id, x.CustomerId }).OnDelete(DeleteBehavior.Restrict);
    }
}
