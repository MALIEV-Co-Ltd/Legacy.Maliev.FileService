namespace Legacy.Maliev.FileService.Application.CustomerDocuments;

/// <summary>Distinct HR-owned employment states; profile, permission, and identity records do not imply active employment.</summary>
public enum DocumentEmployeeEmploymentState
{
    /// <summary>No current verified active or inactive decision.</summary>
    Unknown,
    /// <summary>The current HR decision certifies active employment.</summary>
    Active,
    /// <summary>The current HR decision certifies inactive employment.</summary>
    Inactive,
}

/// <summary>A canonical employee's current HR projection without profile PII or identity-binding authority.</summary>
public sealed record DocumentEmployeeEmployment(int EmployeeId, DocumentEmployeeEmploymentState State, long Revision,
    DateTimeOffset? EffectiveAtUtc, DateTimeOffset? VerifiedAtUtc, string? VerifiedBySubject)
{
    /// <summary>Only a validated active HR projection can satisfy the employment part of eligibility.</summary>
    public bool IsActiveEmployment => State == DocumentEmployeeEmploymentState.Active;
}

/// <summary>Reads employment for the positive canonical employee ID supplied by a genuine Auth projection; never derives IDs from subject or email.</summary>
public interface ICustomerDocumentEmploymentReader
{
    /// <summary>Returns distinct unknown/inactive data only after an authorized exact-ID read; dependency failures provide no projection.</summary>
    Task<CustomerDocumentOwnerRead<DocumentEmployeeEmployment>> ReadAsync(int employeeId, CancellationToken token);
}
