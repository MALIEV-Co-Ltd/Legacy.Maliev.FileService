using Legacy.Maliev.FileService.Domain.CustomerDocuments;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
namespace Legacy.Maliev.FileService.Application.CustomerDocuments;
/// <summary>Evaluates durable internal tasks; never sends notices or resolves customer addresses.</summary>
public sealed class NdaReminderScheduler(INdaRepository repository, INdaAuthority authority, NdaOptions options, TimeProvider clock,
    ILogger<NdaReminderScheduler>? logger = null) : INdaReminderQueue
{
    /// <summary>Queues due/missed tasks with exact renewal-epoch deduplication in persistence.</summary>
    public async Task<int> QueueDueAsync(CancellationToken token)
    {
        if (!options.Enabled || !options.SchedulerEnabled) return 0;
        if (options.LeadDays.Length is 0 or > 20 || options.LeadDays.Any(x => x < 0 || x > 3660) || options.LeadDays.Distinct().Count() != options.LeadDays.Length)
            throw new NdaValidationException();
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(options.TimeZoneId); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { throw new NdaValidationException(); }
        var now = clock.GetUtcNow();
        var count = 0;
        foreach (var record in await repository.ReadCurrentAgreementsAsync(token))
        {
            var recipient = await authority.ValidateActiveEmployeeAsync(record.ResponsibleEmployeeSubject, token);
            if (recipient == DocumentAuthorityOutcome.Denied)
            {
                (logger ?? NullLogger<NdaReminderScheduler>.Instance).LogWarning(
                    "An internal NDA reminder requires responsible staff review and reassignment before scheduling.");
                continue;
            }
            NdaAuthorityGuard.Require(recipient);
            var eventAt = record.RenewalAtUtc ?? record.ExpiresAtUtc;
            if (eventAt is null) continue;
            foreach (var lead in options.LeadDays)
            {
                var due = DueAtUtc(eventAt.Value, lead, zone);
                if (due <= now) count += await repository.QueueReminderAsync(record, lead, due, now, token);
            }
        }
        return count;
    }
    /// <summary>Converts explicit timezone calendar midnight to UTC, rejecting ambiguous local instants.</summary>
    public static DateTimeOffset DueAtUtc(DateTimeOffset eventAt, int leadDays, TimeZoneInfo zone)
    {
        if (leadDays < 0 || leadDays > 3660) throw new NdaValidationException();
        var localMidnight = TimeZoneInfo.ConvertTime(eventAt, zone).Date.AddDays(-leadDays);
        if (zone.IsInvalidTime(localMidnight) || zone.IsAmbiguousTime(localMidnight)) throw new NdaValidationException();
        return new(TimeZoneInfo.ConvertTimeToUtc(localMidnight, zone), TimeSpan.Zero);
    }
    /// <summary>Returns only the current verified responsible employee's bounded in-app worklist.</summary>
    public async Task<IReadOnlyList<InternalNdaReminderSummary>> ReadWorklistAsync(DocumentActor actor, int limit, CancellationToken token,
        DateTimeOffset? dueFromUtc = null, DateTimeOffset? dueThroughUtc = null, InternalNdaReminderState? state = null)
    {
        if (!options.Enabled) throw new DocumentAuthorityUnavailableException();
        if (limit is < 1 or > 100 || dueFromUtc is { Offset: var fromOffset } && fromOffset != TimeSpan.Zero
            || dueThroughUtc is { Offset: var throughOffset } && throughOffset != TimeSpan.Zero
            || dueFromUtc is not null && dueThroughUtc is not null && (dueThroughUtc < dueFromUtc || dueThroughUtc - dueFromUtc > TimeSpan.FromDays(366))
            || state is not null && !Enum.IsDefined(state.Value)) throw new ArgumentException("Invalid worklist filters.");
        var subject = NdaAuthorityGuard.Subject(await authority.AuthorizeWorklistAsync(actor, token), true);
        NdaAuthorityGuard.Require(await authority.ValidateActiveEmployeeAsync(subject, token));
        return await repository.ReadWorklistAsync(subject, limit, token, dueFromUtc, dueThroughUtc, state);
    }
}
