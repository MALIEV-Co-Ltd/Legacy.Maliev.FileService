using Legacy.Maliev.FileService.Application.CustomerDocuments;

namespace Legacy.Maliev.FileService.Data.CustomerDocuments;

/// <summary>Uses the existing redirect-free, cookie-free owner transport with a separate explicit Employee HTTPS origin.</summary>
public sealed class CustomerDocumentEmploymentHttpClientFactory : IDisposable
{
    private readonly CustomerDocumentOwnerHttpClientFactory transport;
    /// <summary>Gets the Employee origin, independent of order, quotation and CRM configuration.</summary>
    public Uri Origin => transport.OrderOrigin;
    /// <summary>Creates an explicit HTTPS root transport without ambient credential or redirect discovery.</summary>
    public CustomerDocumentEmploymentHttpClientFactory(Uri origin) => transport = new(origin, origin);
    private CustomerDocumentEmploymentHttpClientFactory(Uri origin, HttpMessageHandler handler) =>
        transport = CustomerDocumentOwnerHttpClientFactory.CreateForIsolatedLoopbackTests(origin, handler);
    /// <summary>Creates a controlled loopback test transport that cannot be registered as a production origin.</summary>
    public static CustomerDocumentEmploymentHttpClientFactory CreateForIsolatedLoopbackTests(Uri origin, HttpMessageHandler handler) => new(origin, handler);
    internal Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => transport.SendAsync(request, token);
    /// <summary>Disposes the owned transport.</summary>
    public void Dispose() => transport.Dispose();
}

/// <summary>Unimplemented Documents consumer of the source-only HR employment route; no runtime authority or identity binding is established.</summary>
public sealed class CustomerDocumentEmploymentHttpReader : ICustomerDocumentEmploymentReader
{
    /// <summary>Stages an optional server credential, bounded timeout, and deterministic UTC clock without registering a transport binding.</summary>
    public CustomerDocumentEmploymentHttpReader(CustomerDocumentEmploymentHttpClientFactory factory,
        ICustomerDocumentOwnerCredential? credential = null, TimeSpan? timeout = null, TimeProvider? timeProvider = null)
    {
        _ = factory; _ = credential; _ = timeProvider;
        var duration = timeout ?? TimeSpan.FromSeconds(10);
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(timeout));
    }
    /// <inheritdoc />
    public Task<CustomerDocumentOwnerRead<DocumentEmployeeEmployment>> ReadAsync(int employeeId, CancellationToken token)
    {
        _ = employeeId; _ = token;
        throw new NotImplementedException();
    }
}
