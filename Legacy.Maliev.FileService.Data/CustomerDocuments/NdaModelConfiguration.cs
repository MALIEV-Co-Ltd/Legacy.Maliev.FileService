using Legacy.Maliev.FileService.Domain.CustomerDocuments;
using Microsoft.EntityFrameworkCore;
namespace Legacy.Maliev.FileService.Data.CustomerDocuments;
/// <summary>Defines additive immutable NDA evidence and durable internal worklist constraints.</summary>
public static class NdaModelConfiguration
{
    /// <summary>Applies the model through the registry context's owner integration.</summary>
    public static void Configure(ModelBuilder model)
    {
        var n = model.Entity<NdaRecord>();
        n.ToTable("NdaRecord", table =>
        {
            table.HasCheckConstraint("CK_NdaRecord_Revision", "\"VerificationRevision\" > 0");
            table.HasCheckConstraint("CK_NdaRecord_Customer", "\"CustomerId\" > 0");
            table.HasCheckConstraint("CK_NdaRecord_Parties", "length(btrim(\"PartyOne\")) > 0 AND length(btrim(\"PartyTwo\")) > 0 AND lower(btrim(\"PartyOne\")) <> lower(btrim(\"PartyTwo\"))");
            table.HasCheckConstraint("CK_NdaRecord_Staff", "length(btrim(\"ResponsibleEmployeeSubject\")) > 0 AND length(btrim(\"VerifiedBySubject\")) > 0");
            table.HasCheckConstraint("CK_NdaRecord_Dates", "(\"ExpiresAtUtc\" IS NULL OR \"ExpiresAtUtc\" > \"EffectiveAtUtc\") AND (\"RenewalAtUtc\" IS NULL OR \"RenewalAtUtc\" >= \"EffectiveAtUtc\")");
            table.HasCheckConstraint("CK_NdaRecord_Survival", "(\"SurvivalKind\" IN (0,1) AND \"SurvivalEndsAtUtc\" IS NULL) OR (\"SurvivalKind\" = 2 AND \"SurvivalEndsAtUtc\" IS NOT NULL AND \"SurvivalEndsAtUtc\" >= COALESCE(\"ExpiresAtUtc\",\"EffectiveAtUtc\"))");
        });
        n.HasKey(x => x.Id);
        n.Property(x => x.CoverageSealed).HasDefaultValue(false);
        n.HasAlternateKey(x => new { x.Id, x.CustomerId });
        n.HasAlternateKey(x => new { x.Id, x.DocumentId, x.CustomerId });
        n.HasAlternateKey(x => new { x.Id, x.VersionId, x.VerificationRevision });
        n.HasAlternateKey(x => new { x.Id, x.VersionId, x.VerificationRevision, x.ResponsibleEmployeeSubject });
        n.HasIndex(x => new { x.DocumentId, x.VerificationRevision }).IsUnique();
        n.HasIndex(x => x.SupersedesNdaId).IsUnique().HasFilter("\"SupersedesNdaId\" IS NOT NULL");
        n.Property(x => x.PartyOne).HasMaxLength(500).IsRequired();
        n.Property(x => x.PartyTwo).HasMaxLength(500).IsRequired();
        n.Property(x => x.ResponsibleEmployeeSubject).HasMaxLength(200).IsRequired();
        n.Property(x => x.VerifiedBySubject).HasMaxLength(200).IsRequired();
        n.HasOne<CustomerDocumentVersion>().WithMany().HasForeignKey(x => new { x.VersionId, x.CustomerId })
            .HasPrincipalKey(x => new { x.Id, x.CustomerId }).OnDelete(DeleteBehavior.Restrict);
        n.HasOne<CustomerDocumentVersion>().WithMany().HasForeignKey(x => new { x.VersionId, x.DocumentId })
            .HasPrincipalKey(x => new { x.Id, x.DocumentId }).OnDelete(DeleteBehavior.Restrict);
        n.HasOne<DocumentVerificationEvidence>().WithMany().HasForeignKey(x => new { x.VersionId, x.VerificationRevision })
            .HasPrincipalKey(x => new { x.VersionId, x.Revision }).OnDelete(DeleteBehavior.Restrict);
        n.HasOne<NdaRecord>().WithMany().HasForeignKey(x => new { x.SupersedesNdaId, x.DocumentId, x.CustomerId })
            .HasPrincipalKey(x => new { x.Id, x.DocumentId, x.CustomerId }).OnDelete(DeleteBehavior.Restrict);
        var c = model.Entity<NdaCoverage>();
        c.ToTable("NdaCoverage", table =>
        {
            table.HasCheckConstraint("CK_NdaCoverage_Identity", "\"ResourceId\" > 0 AND \"Kind\" BETWEEN 0 AND 5");
            table.HasCheckConstraint("CK_NdaCoverage_Customer", "\"Kind\" <> 0 OR \"ResourceId\" = \"CustomerId\"");
        });
        c.HasKey(x => new { x.NdaId, x.Kind, x.ResourceId });
        c.HasOne<NdaRecord>().WithMany().HasForeignKey(x => new { x.NdaId, x.CustomerId })
            .HasPrincipalKey(x => new { x.Id, x.CustomerId }).OnDelete(DeleteBehavior.Restrict);
        c.HasIndex(x => new { x.CustomerId, x.Kind, x.ResourceId });
        var r = model.Entity<InternalNdaReminder>();
        r.ToTable("InternalNdaReminder", table =>
        {
            table.HasCheckConstraint("CK_InternalNdaReminder_Lead", "\"LeadDays\" BETWEEN 0 AND 3660");
            table.HasCheckConstraint("CK_InternalNdaReminder_State", "\"State\" BETWEEN 0 AND 2 AND ((\"State\" = 2 AND \"CancelledAtUtc\" IS NOT NULL) OR (\"State\" <> 2 AND \"CancelledAtUtc\" IS NULL))");
            table.HasCheckConstraint("CK_InternalNdaReminder_Recipient", "length(btrim(\"ResponsibleEmployeeSubject\")) > 0");
        });
        r.HasKey(x => x.Id);
        r.HasIndex(x => new { x.NdaId, x.VersionId, x.RenewalRevision, x.LeadDays }).IsUnique();
        r.Property(x => x.ResponsibleEmployeeSubject).HasMaxLength(200).IsRequired();
        r.HasOne<NdaRecord>().WithMany().HasForeignKey(x => new { x.NdaId, x.VersionId, x.RenewalRevision, x.ResponsibleEmployeeSubject })
            .HasPrincipalKey(x => new { x.Id, x.VersionId, x.VerificationRevision, x.ResponsibleEmployeeSubject }).OnDelete(DeleteBehavior.Restrict);
        r.HasIndex(x => new { x.ResponsibleEmployeeSubject, x.DueAtUtc });
    }
}
