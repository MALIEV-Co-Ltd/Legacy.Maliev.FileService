using Legacy.Maliev.FileService.Domain.CustomerDocuments;
using Microsoft.EntityFrameworkCore;
namespace Legacy.Maliev.FileService.Data.CustomerDocuments;
/// <summary>Persists the additive registry separately from the legacy Upload mapping.</summary>
public sealed class CustomerDocumentDbContext(DbContextOptions<CustomerDocumentDbContext> options) : DbContext(options)
{
    /// <summary>Gets the durable Documents registry records.</summary>
    public DbSet<CustomerDocument> Documents => Set<CustomerDocument>();
    /// <summary>Gets the durable Versions registry records.</summary>
    public DbSet<CustomerDocumentVersion> Versions => Set<CustomerDocumentVersion>();
    /// <summary>Gets the durable Associations registry records.</summary>
    public DbSet<DocumentAssociation> Associations => Set<DocumentAssociation>();
    /// <summary>Gets the durable Audits registry records.</summary>
    public DbSet<DocumentAudit> Audits => Set<DocumentAudit>();
    /// <summary>Gets the durable Verifications registry records.</summary>
    public DbSet<DocumentVerificationEvidence> Verifications => Set<DocumentVerificationEvidence>();
    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        CustomerDocumentModelConfiguration.Configure(modelBuilder);
        ProtectedDocumentPersistenceConfiguration.Configure(modelBuilder);
        NdaModelConfiguration.Configure(modelBuilder);
    }
    /// <inheritdoc />
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardImmutableEvidence();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }
    /// <inheritdoc />
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        GuardImmutableEvidence();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }
    private void GuardImmutableEvidence()
    {
        ChangeTracker.DetectChanges();
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Modified or EntityState.Deleted)) continue;
            if (entry.Entity is CustomerDocumentVersion && entry.State == EntityState.Modified &&
                entry.Properties.Where(x => x.IsModified).All(x => x.Metadata.Name == nameof(CustomerDocumentVersion.AssociationsSealed)) &&
                entry.Property(nameof(CustomerDocumentVersion.AssociationsSealed)).OriginalValue is false &&
                entry.Property(nameof(CustomerDocumentVersion.AssociationsSealed)).CurrentValue is true) continue;
            if (entry.Entity is CustomerDocumentVersion or DocumentAssociation or DocumentAudit or DocumentVerificationEvidence)
                throw new InvalidOperationException("Document evidence is append-only.");
            if (entry.Entity is NdaRecord && entry.State == EntityState.Modified &&
                entry.Properties.Where(x => x.IsModified).All(x => x.Metadata.Name == nameof(NdaRecord.CoverageSealed)) &&
                entry.Property(nameof(NdaRecord.CoverageSealed)).OriginalValue is false &&
                entry.Property(nameof(NdaRecord.CoverageSealed)).CurrentValue is true) continue;
            if (entry.Entity is NdaRecord or NdaCoverage)
                throw new InvalidOperationException("NDA evidence is append-only.");
            if (entry.Entity is InternalNdaReminder reminder && (entry.State == EntityState.Deleted ||
                entry.Properties.Any(x => x.IsModified && x.Metadata.Name is not (nameof(InternalNdaReminder.State) or nameof(InternalNdaReminder.CancelledAtUtc))) ||
                !((reminder.State == InternalNdaReminderState.Cancelled && reminder.CancelledAtUtc is not null &&
                   entry.Property(nameof(InternalNdaReminder.State)).OriginalValue is InternalNdaReminderState.Due or InternalNdaReminderState.Missed) ||
                  (reminder.State == InternalNdaReminderState.Missed && reminder.CancelledAtUtc is null &&
                   entry.Property(nameof(InternalNdaReminder.State)).OriginalValue is InternalNdaReminderState.Due))))
                throw new InvalidOperationException("Reminder identity is immutable and cancellation is irreversible.");
            if (entry.Entity is ProtectedDocumentUploadCheckpoint && (entry.State == EntityState.Deleted ||
                entry.Properties.Any(x => x.IsModified && x.Metadata.Name is not (nameof(ProtectedDocumentUploadCheckpoint.State) or nameof(ProtectedDocumentUploadCheckpoint.CompletedRevision)))))
                throw new InvalidOperationException("Upload reservation identity is immutable.");
            if (entry.Entity is CustomerDocument && (entry.State == EntityState.Deleted ||
                entry.Properties.Any(x => x.IsModified && x.Metadata.Name is not (nameof(CustomerDocument.Revision) or nameof(CustomerDocument.ArchivedAtUtc)))))
                throw new InvalidOperationException("Document identity and classification are immutable.");
        }
    }
    internal async Task InsertAndSealVersionAsync(CustomerDocumentVersion version, IReadOnlyList<DocumentAssociation> associations, CancellationToken token)
    {
        if (version.AssociationsSealed) throw new ArgumentException("A version must be sealed by atomic finalization.");
        var ownsTransaction = Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction ? await Database.BeginTransactionAsync(token) : null;
        Versions.Add(version);
        Associations.AddRange(associations);
        await SaveChangesAsync(token);
        version.AssociationsSealed = true;
        await SaveChangesAsync(token);
        if (transaction is not null) await transaction.CommitAsync(token);
    }
}
