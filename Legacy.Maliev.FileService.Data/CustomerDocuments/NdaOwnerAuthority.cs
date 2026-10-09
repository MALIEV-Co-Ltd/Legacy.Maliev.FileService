using Legacy.Maliev.FileService.Application.CustomerDocuments;
namespace Legacy.Maliev.FileService.Data.CustomerDocuments;
/// <summary>Joins current customer authority, canonical owner scope and the accepted active-employee boundary.</summary>
public sealed class NdaOwnerAuthority(ICustomerDocumentAuthority customers, NdaCanonicalCoverageResolver coverage,
    INdaEmployeeAuthority? employees = null) : INdaAuthority
{
    /// <inheritdoc />
    public Task<DocumentAuthorityDecision> AuthorizeAsync(DocumentActor actor, int customerId, string permission, CancellationToken token) =>
        customers.AuthorizeAsync(actor, customerId, permission, token);
    /// <inheritdoc />
    public Task<DocumentAuthorityOutcome> ValidateActiveEmployeeAsync(string subject, CancellationToken token) =>
        employees is null || string.IsNullOrWhiteSpace(subject) ? Task.FromResult(DocumentAuthorityOutcome.Unavailable) :
            employees.ValidateActiveEmployeeAsync(subject, token);
    /// <inheritdoc />
    public Task<CanonicalNdaResources> ResolveCoverageAsync(DocumentActor actor, int customerId,
        IReadOnlyList<NdaCoverageRequest> resources, Guid? versionId, CancellationToken token)
    {
        _ = actor; // Current-session permission is checked immediately before this owner read by every service entry point.
        return coverage.ResolveAsync(customerId, resources, versionId, token);
    }
    /// <inheritdoc />
    public Task<DocumentAuthorityDecision> AuthorizeWorklistAsync(DocumentActor actor, CancellationToken token) =>
        employees is null ? Task.FromResult(new DocumentAuthorityDecision(DocumentAuthorityOutcome.Unavailable)) :
            employees.AuthorizeWorklistAsync(actor, token);
    /// <inheritdoc />
    public Task<CanonicalNdaResources> ResolveProtectionCoverageAsync(DocumentActor actor, int customerId,
        IReadOnlyList<NdaCoverageRequest> resources, Guid? versionId, CancellationToken token)
    {
        _ = actor;
        return coverage.ResolveProtectionAsync(customerId, resources, versionId, token);
    }
}
