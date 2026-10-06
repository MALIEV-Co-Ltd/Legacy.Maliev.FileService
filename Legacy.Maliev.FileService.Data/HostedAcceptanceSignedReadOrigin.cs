using Google.Cloud.Storage.V1;

namespace Legacy.Maliev.FileService.Data;

/// <summary>Applies the admitted origin to genuine SDK signing options without rewriting signed URLs.</summary>
public sealed class HostedAcceptanceSignedReadOrigin
{
    private readonly Uri origin;
    private readonly HostedAcceptanceStorageTransport guard;

    /// <summary>Creates a signing origin bound to one independently admitted finite endpoint.</summary>
    public HostedAcceptanceSignedReadOrigin(Uri origin, DateTimeOffset expiresUtc, TimeProvider clock)
    {
        guard = new HostedAcceptanceStorageTransport(origin, expiresUtc, clock);
        this.origin = origin;
    }

    /// <summary>Returns V4 options bound to the exact admitted scheme, host and port.</summary>
    public UrlSigner.Options Apply(UrlSigner.Options options)
    {
        guard.ValidateRequest(origin);
        return options.WithScheme(origin.Scheme).WithHost(origin.Host).WithPort(origin.Port);
    }

    /// <summary>Validates the actual signed URL before returning it to an acceptance consumer.</summary>
    public Uri ValidateSignedUri(string value)
    {
        var result = new Uri(value, UriKind.Absolute);
        guard.ValidateRequest(result);
        return result;
    }
}
