using Legacy.Maliev.FileService.Domain.CustomerDocuments;
using Microsoft.EntityFrameworkCore;
namespace Legacy.Maliev.FileService.Data.CustomerDocuments;
/// <summary>Defines additive registry foreign keys, checks and immutable evidence indexes.</summary>
public static class CustomerDocumentModelConfiguration
{
    /// <summary>Applies the dedicated registry model without changing legacy Upload entities.</summary>
    public static void Configure(ModelBuilder model)
    {
        var d = model.Entity<CustomerDocument>();
        d.ToTable("CustomerDocument", t =>
        {
            t.HasCheckConstraint("CK_CustomerDocument_Customer", "\"CustomerId\" > 0");
            t.HasCheckConstraint("CK_CustomerDocument_Revision", "\"Revision\" > 0");
            t.HasCheckConstraint("CK_CustomerDocument_Kind", "\"Kind\" BETWEEN 0 AND 6");
            t.HasCheckConstraint("CK_CustomerDocument_Visibility", "\"Visibility\" BETWEEN 0 AND 1");
        });
        d.HasKey(x => x.Id);
        d.HasAlternateKey(x => new { x.Id, x.CustomerId });
        d.HasAlternateKey(x => new { x.Id, x.CustomerId, x.Kind });
        d.Property(x => x.Title).HasMaxLength(250).IsRequired();
        d.Property(x => x.Revision).IsConcurrencyToken();
        var v = model.Entity<CustomerDocumentVersion>();
        v.ToTable("CustomerDocumentVersion", t =>
        {
            t.HasCheckConstraint("CK_CustomerDocumentVersion_Number", "\"VersionNumber\" > 0");
            t.HasCheckConstraint("CK_CustomerDocumentVersion_Revision", "\"Revision\" > 0");
            t.HasCheckConstraint("CK_CustomerDocumentVersion_Hash", "\"ContentSha256\" ~ '^[0-9a-f]{64}$'");
            t.HasCheckConstraint("CK_CustomerDocumentVersion_Kind", "\"Kind\" BETWEEN 0 AND 6");
            t.HasCheckConstraint("CK_CustomerDocumentVersion_Actor", "length(btrim(\"ActorSubject\")) > 0");
            t.HasCheckConstraint("CK_CustomerDocumentVersion_Generation", "\"StorageGeneration\" > 0 AND \"ScanSourceGeneration\" > 0");
            t.HasCheckConstraint("CK_CustomerDocumentVersion_Size", "\"ContentSize\" BETWEEN 1 AND 20971520");
            t.HasCheckConstraint("CK_CustomerDocumentVersion_Type", "\"ContentType\" IN ('application/pdf','image/png','image/jpeg')");
            t.HasCheckConstraint("CK_CustomerDocumentVersion_Storage", "length(btrim(\"StorageBucket\")) > 0 AND \"StorageObjectName\" = 'customer-documents/' || \"CustomerId\"::text || '/' || replace(\"DocumentId\"::text,'-','') || '/' || replace(\"Id\"::text,'-','') || '/original'");
            t.HasCheckConstraint("CK_CustomerDocumentVersion_Scan", "\"ScanOperationId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
            t.HasCheckConstraint("CK_CustomerDocumentVersion_Filename", "length(btrim(\"OriginalFileName\")) > 0");
        });
        v.HasKey(x => x.Id);
        v.HasAlternateKey(x => new { x.Id, x.CustomerId });
        v.HasAlternateKey(x => new { x.Id, x.DocumentId });
        v.HasIndex(x => new { x.DocumentId, x.VersionNumber }).IsUnique();
        v.Property(x => x.ContentSha256).HasMaxLength(64).IsRequired();
        v.Property(x => x.ActorSubject).HasMaxLength(200).IsRequired();
        v.Property(x => x.StorageBucket).HasMaxLength(255).IsRequired();
        v.Property(x => x.StorageObjectName).HasMaxLength(1024).IsRequired();
        v.Property(x => x.ContentType).HasMaxLength(50).IsRequired();
        v.Property(x => x.OriginalFileName).HasMaxLength(250).IsRequired();
        v.HasOne<CustomerDocument>().WithMany().HasForeignKey(x => new { x.DocumentId, x.CustomerId, x.Kind })
            .HasPrincipalKey(x => new { x.Id, x.CustomerId, x.Kind }).OnDelete(DeleteBehavior.Restrict);
        var a = model.Entity<DocumentAssociation>();
        a.ToTable("CustomerDocumentAssociation", t =>
        {
            t.HasCheckConstraint("CK_CustomerDocumentAssociation_Id", "\"ResourceId\" > 0");
            t.HasCheckConstraint("CK_CustomerDocumentAssociation_Kind", "\"Kind\" BETWEEN 0 AND 5");
            t.HasCheckConstraint("CK_CustomerDocumentAssociation_Customer", "\"Kind\" <> 0 OR \"ResourceId\" = \"CustomerId\"");
        });
        a.HasKey(x => new { x.VersionId, x.Kind, x.ResourceId });
        a.HasIndex(x => x.VersionId).IsUnique().HasFilter("\"Kind\" = 2");
        a.HasOne<CustomerDocumentVersion>().WithMany().HasForeignKey(x => new { x.VersionId, x.CustomerId })
            .HasPrincipalKey(x => new { x.Id, x.CustomerId }).OnDelete(DeleteBehavior.Restrict);
        var e = model.Entity<DocumentVerificationEvidence>();
        e.ToTable("CustomerDocumentVerificationEvidence", t =>
        {
            t.HasCheckConstraint("CK_CustomerDocumentVerificationEvidence_Revision", "\"Revision\" > 0");
            t.HasCheckConstraint("CK_CustomerDocumentVerificationEvidence_Status", "\"Status\" BETWEEN 0 AND 2");
            t.HasCheckConstraint("CK_CustomerDocumentVerificationEvidence_Verifier",
                "(\"Status\" = 0 AND \"VerifiedBySubject\" IS NULL AND \"VerifiedAtUtc\" IS NULL) OR (\"Status\" IN (1,2) AND \"VerifiedBySubject\" IS NOT NULL AND length(btrim(\"VerifiedBySubject\")) > 0 AND \"VerifiedAtUtc\" IS NOT NULL)");
        });
        e.HasKey(x => new { x.VersionId, x.Revision });
        e.Property(x => x.VerifiedBySubject).HasMaxLength(200);
        e.HasOne<CustomerDocumentVersion>().WithMany().HasForeignKey(x => x.VersionId).OnDelete(DeleteBehavior.Restrict);
        var audit = model.Entity<DocumentAudit>();
        audit.ToTable("CustomerDocumentAudit", t =>
        {
            t.HasCheckConstraint("CK_CustomerDocumentAudit_Revision", "\"Revision\" > 0");
            t.HasCheckConstraint("CK_CustomerDocumentAudit_Actor", "length(btrim(\"ActorSubject\")) > 0");
        });
        audit.HasKey(x => x.Id);
        audit.Property(x => x.ActorSubject).HasMaxLength(200).IsRequired();
        audit.Property(x => x.Action).HasMaxLength(100).IsRequired();
        audit.Property(x => x.Reason).HasMaxLength(2000);
        audit.HasOne<CustomerDocument>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Restrict);
        audit.HasOne<CustomerDocumentVersion>().WithMany().HasForeignKey(x => new { x.VersionId, x.DocumentId })
            .HasPrincipalKey(x => new { x.Id, x.DocumentId }).OnDelete(DeleteBehavior.Restrict);
    }
}
