using Legacy.Maliev.FileService.Application.CustomerDocuments;
using Legacy.Maliev.FileService.Domain.CustomerDocuments;
using Microsoft.EntityFrameworkCore;
namespace Legacy.Maliev.FileService.Data.CustomerDocuments;
/// <summary>Serializes NDA renewal and internal task epochs with a PostgreSQL transaction lock.</summary>
public sealed class NdaRepository(CustomerDocumentDbContext context) : INdaRepository
{
    // All registry NDA writers use this lock; no application lock can protect cross-process writers.
    private const long EpochLock = 1852077194;
    /// <inheritdoc />
    public async Task<NdaAgreementReadback?> ReadAgreementAsync(int customerId, Guid documentId, Guid? versionId, CancellationToken token)
    {
        var record = await context.Set<NdaRecord>().AsNoTracking().Where(x => x.CustomerId == customerId && x.DocumentId == documentId &&
            (versionId == null || x.VersionId == versionId)).OrderByDescending(x => x.VerificationRevision).FirstOrDefaultAsync(token);
        if (record is null) return null;
        if (!record.CoverageSealed) throw new DocumentAuthorityUnavailableException();
        return new(record, await context.Set<NdaRecord>().AnyAsync(x => x.SupersedesNdaId == record.Id, token));
    }
    /// <inheritdoc />
    public async Task<NdaVerificationTarget?> ReadVerificationTargetAsync(int customerId, Guid documentId, Guid versionId, CancellationToken token) =>
        await (from v in context.Versions.AsNoTracking()
               join d in context.Documents.AsNoTracking() on v.DocumentId equals d.Id
               where v.Id == versionId && v.DocumentId == documentId && v.CustomerId == customerId && d.CustomerId == customerId && d.Kind == DocumentKind.Nda
               select new NdaVerificationTarget(d.Id, v.Id, customerId, v.Kind, v.ContentSha256, d.Revision)).SingleOrDefaultAsync(token);
    /// <inheritdoc />
    public async Task<NdaRecord> AppendVerificationAsync(NdaRecord record, IReadOnlyList<NdaCoverageRequest> coverage,
        long expectedRevision, string reason, CancellationToken token)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(token);
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({EpochLock})", token);
        var target = await ReadVerificationTargetAsync(record.CustomerId, record.DocumentId, record.VersionId, token) ?? throw new NdaNotFoundException();
        if (target.Revision != expectedRevision) throw new NdaRevisionConflictException();
        var nextRevision = checked(expectedRevision + 1);
        var updated = await context.Documents.Where(x => x.Id == record.DocumentId && x.CustomerId == record.CustomerId && x.Revision == expectedRevision)
            .ExecuteUpdateAsync(x => x.SetProperty(d => d.Revision, nextRevision), token);
        if (updated != 1) throw new NdaRevisionConflictException();
        var prior = await context.Set<NdaRecord>().AsNoTracking().Where(x => x.DocumentId == record.DocumentId && x.CustomerId == record.CustomerId)
            .OrderByDescending(x => x.VerificationRevision).FirstOrDefaultAsync(token);
        if (prior is { CoverageSealed: false }) throw new DocumentAuthorityUnavailableException();
        if (record.CoverageSealed) throw new ArgumentException("Coverage must be sealed by atomic verification.");
        record.VerificationRevision = nextRevision;
        record.SupersedesNdaId = prior?.Id;
        context.Verifications.Add(new DocumentVerificationEvidence
        {
            VersionId = record.VersionId,
            Revision = nextRevision,
            Status = VerificationStatus.Verified,
            VerifiedBySubject = record.VerifiedBySubject,
            VerifiedAtUtc = record.VerifiedAtUtc
        });
        context.Set<NdaRecord>().Add(record);
        context.Set<NdaCoverage>().AddRange(coverage.Select(x => new NdaCoverage { NdaId = record.Id, CustomerId = record.CustomerId, Kind = x.Kind, ResourceId = x.ResourceId }));
        context.Audits.Add(new DocumentAudit
        {
            Id = Guid.NewGuid(),
            DocumentId = record.DocumentId,
            VersionId = record.VersionId,
            ActorSubject = record.VerifiedBySubject,
            AtUtc = record.VerifiedAtUtc,
            Action = "NdaVerified",
            Reason = reason,
            Revision = nextRevision
        });
        if (prior is not null)
        {
            await context.Set<InternalNdaReminder>().Where(x => x.NdaId == prior.Id && x.DueAtUtc > record.VerifiedAtUtc && x.State != InternalNdaReminderState.Cancelled)
                .ExecuteUpdateAsync(x => x.SetProperty(r => r.State, InternalNdaReminderState.Cancelled)
                    .SetProperty(r => r.CancelledAtUtc, record.VerifiedAtUtc), token);
            context.Audits.Add(new DocumentAudit
            {
                Id = Guid.NewGuid(),
                DocumentId = record.DocumentId,
                VersionId = prior.VersionId,
                ActorSubject = record.VerifiedBySubject,
                AtUtc = record.VerifiedAtUtc,
                Action = "NdaFutureRemindersCancelled",
                Reason = reason,
                Revision = nextRevision
            });
        }
        await context.SaveChangesAsync(token);
        record.CoverageSealed = true;
        await context.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return record;
    }
    /// <inheritdoc />
    public async Task<IReadOnlyList<NdaRecord>> ReadCoveredAgreementsAsync(int customerId, IReadOnlyList<NdaCoverageRequest> resources,
        Guid? versionId, CancellationToken token)
    {
        // Exact version is authoritative scope evidence, never a filter erasing old agreement coverage.
        _ = versionId;
        if (await context.Set<NdaRecord>().AnyAsync(x => x.CustomerId == customerId && !x.CoverageSealed, token))
            throw new DocumentAuthorityUnavailableException();
        var covers = await context.Set<NdaCoverage>().AsNoTracking().Where(x => x.CustomerId == customerId).ToListAsync(token);
        var ids = covers.Where(x => x.Kind == DocumentResourceKind.Customer && x.ResourceId == customerId ||
            resources.Contains(new(x.Kind, x.ResourceId))).Select(x => x.NdaId).Distinct().ToArray();
        return await context.Set<NdaRecord>().AsNoTracking().Where(x => x.CustomerId == customerId && ids.Contains(x.Id)).ToListAsync(token);
    }
    /// <inheritdoc />
    public async Task<IReadOnlyList<NdaRecord>> ReadCurrentAgreementsAsync(CancellationToken token)
    {
        if (await context.Set<NdaRecord>().AnyAsync(x => !x.CoverageSealed, token)) throw new DocumentAuthorityUnavailableException();
        return await context.Set<NdaRecord>().AsNoTracking().Where(x => !context.Set<NdaRecord>().Any(n => n.SupersedesNdaId == x.Id)).ToListAsync(token);
    }
    /// <inheritdoc />
    public async Task<int> QueueReminderAsync(NdaRecord record, int leadDays, DateTimeOffset dueAtUtc, DateTimeOffset now, CancellationToken token)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(token);
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({EpochLock})", token);
        var current = await context.Set<NdaRecord>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == record.Id &&
            x.VersionId == record.VersionId && x.VerificationRevision == record.VerificationRevision &&
            !context.Set<NdaRecord>().Any(n => n.SupersedesNdaId == x.Id), token);
        if (current is null || current.ResponsibleEmployeeSubject != record.ResponsibleEmployeeSubject) return 0;
        if (!current.CoverageSealed) throw new DocumentAuthorityUnavailableException();
        var state = (int)(now >= dueAtUtc.AddDays(1) ? InternalNdaReminderState.Missed : InternalNdaReminderState.Due);
        if (state == (int)InternalNdaReminderState.Missed)
        {
            var updated = await context.Set<InternalNdaReminder>().Where(x => x.NdaId == current.Id && x.VersionId == current.VersionId &&
                x.RenewalRevision == current.VerificationRevision && x.LeadDays == leadDays && x.State == InternalNdaReminderState.Due)
                .ExecuteUpdateAsync(x => x.SetProperty(r => r.State, InternalNdaReminderState.Missed), token);
            if (updated > 0)
            {
                context.Audits.Add(new DocumentAudit
                {
                    Id = Guid.NewGuid(),
                    DocumentId = current.DocumentId,
                    VersionId = current.VersionId,
                    ActorSubject = "system:nda-reminder-scheduler",
                    AtUtc = now,
                    Action = "NdaReminderMissed",
                    Revision = current.VerificationRevision
                });
                await context.SaveChangesAsync(token);
            }
        }
        var id = Guid.NewGuid();
        var count = await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "InternalNdaReminder" ("Id", "NdaId", "VersionId", "RenewalRevision", "LeadDays", "ResponsibleEmployeeSubject", "DueAtUtc", "CreatedAtUtc", "State")
            VALUES ({id}, {current.Id}, {current.VersionId}, {current.VerificationRevision}, {leadDays}, {current.ResponsibleEmployeeSubject}, {dueAtUtc}, {now}, {state})
            ON CONFLICT ("NdaId", "VersionId", "RenewalRevision", "LeadDays") DO NOTHING
            """, token);
        if (count == 1)
        {
            context.Audits.Add(new DocumentAudit
            {
                Id = Guid.NewGuid(),
                DocumentId = current.DocumentId,
                VersionId = current.VersionId,
                ActorSubject = "system:nda-reminder-scheduler",
                AtUtc = now,
                Action = "NdaReminderQueued",
                Revision = current.VerificationRevision,
                Reason = $"NdaId={current.Id:D};ReminderId={id:D};LeadDays={leadDays}"
            });
            await context.SaveChangesAsync(token);
        }
        await transaction.CommitAsync(token);
        return count;
    }
    /// <inheritdoc />
    public async Task<IReadOnlyList<InternalNdaReminderSummary>> ReadWorklistAsync(string responsibleSubject, int limit, CancellationToken token,
        DateTimeOffset? dueFromUtc = null, DateTimeOffset? dueThroughUtc = null, InternalNdaReminderState? state = null) =>
        await context.Set<InternalNdaReminder>().AsNoTracking().Where(x => x.ResponsibleEmployeeSubject == responsibleSubject
            && (dueFromUtc == null || x.DueAtUtc >= dueFromUtc) && (dueThroughUtc == null || x.DueAtUtc <= dueThroughUtc)
            && (state == null || x.State == state))
            .OrderBy(x => x.State == InternalNdaReminderState.Cancelled).ThenBy(x => x.DueAtUtc).Take(limit)
            .Select(x => new InternalNdaReminderSummary(x.Id, x.NdaId, x.VersionId, x.RenewalRevision, x.LeadDays,
                x.ResponsibleEmployeeSubject, x.DueAtUtc, x.State)).ToListAsync(token);
}
