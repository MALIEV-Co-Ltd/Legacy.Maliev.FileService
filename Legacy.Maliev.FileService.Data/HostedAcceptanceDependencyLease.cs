namespace Legacy.Maliev.FileService.Data;

/// <summary>Restricts the actual acceptance scanner to the independently admitted resource lifetime.</summary>
public sealed class HostedAcceptanceDependencyLease(DateTimeOffset expiresUtc, TimeProvider clock)
{
    /// <summary>Gets whether this admitted host may still use its scanner.</summary>
    public bool IsCurrent => clock.GetUtcNow() < expiresUtc;
}
