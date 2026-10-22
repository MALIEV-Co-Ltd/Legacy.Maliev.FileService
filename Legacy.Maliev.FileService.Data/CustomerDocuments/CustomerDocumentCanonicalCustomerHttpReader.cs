using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Legacy.Maliev.FileService.Application.CustomerDocuments;

namespace Legacy.Maliev.FileService.Data.CustomerDocuments;

/// <summary>Uses the existing strict redirect-free transport for one explicit CRM origin.</summary>
public sealed class CustomerDocumentCanonicalCustomerHttpClientFactory : IDisposable
{
    private readonly CustomerDocumentOwnerHttpClientFactory transport;
    /// <summary>Gets the explicit HTTPS CRM origin, independent of order and quotation configuration.</summary>
    public Uri Origin => transport.OrderOrigin;
    /// <summary>Creates the bounded server transport; ambient cookies and redirects are disabled.</summary>
    public CustomerDocumentCanonicalCustomerHttpClientFactory(Uri origin)
    {
        transport = new(origin, origin);
    }
    private CustomerDocumentCanonicalCustomerHttpClientFactory(Uri origin, HttpMessageHandler handler)
    {
        transport = CustomerDocumentOwnerHttpClientFactory.CreateForIsolatedLoopbackTests(origin, handler);
    }
    /// <summary>Creates a controlled loopback fixture that is never eligible for production registration.</summary>
    public static CustomerDocumentCanonicalCustomerHttpClientFactory CreateForIsolatedLoopbackTests(Uri origin, HttpMessageHandler handler) =>
        new(origin, handler);
    internal Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => transport.SendAsync(request, token);
    /// <summary>Disposes the owned transport.</summary>
    public void Dispose() => transport.Dispose();
}

/// <summary>Reads source-pinned canonical customer existence only; current member authority remains separate.</summary>
public sealed class CustomerDocumentCanonicalCustomerHttpReader : ICanonicalDocumentCustomerReader
{
    private const int MaximumPayloadBytes = 65536;
    private readonly CustomerDocumentCanonicalCustomerHttpClientFactory factory;
    private readonly ICustomerDocumentOwnerCredential? credential;
    private readonly TimeSpan timeout;
    /// <summary>Creates an optional server-credential reader with an explicit bounded timeout.</summary>
    public CustomerDocumentCanonicalCustomerHttpReader(CustomerDocumentCanonicalCustomerHttpClientFactory factory,
        ICustomerDocumentOwnerCredential? credential = null, TimeSpan? timeout = null)
    {
        this.factory = factory;
        this.credential = credential;
        this.timeout = timeout ?? TimeSpan.FromSeconds(10);
        if (this.timeout <= TimeSpan.Zero || this.timeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(timeout));
    }
    /// <inheritdoc />
    public async Task<CustomerDocumentOwnerRead<CanonicalDocumentCustomer>> ReadAsync(int customerId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (customerId <= 0 || credential is null) return Unavailable();
        using var boundedTime = CancellationTokenSource.CreateLinkedTokenSource(token);
        boundedTime.CancelAfter(timeout);
        try
        {
            var accessToken = await credential.GetAccessTokenAsync(factory.Origin, boundedTime.Token);
            boundedTime.Token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(accessToken) || accessToken.Length > 16384) return Unavailable();
            var relativePath = "customers/" + customerId.ToString(CultureInfo.InvariantCulture);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(factory.Origin, relativePath));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await factory.SendAsync(request, boundedTime.Token);
            boundedTime.Token.ThrowIfCancellationRequested();
            if (response.RequestMessage?.RequestUri is { } actual && actual != request.RequestUri) return Unavailable();
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                return new(DocumentAuthorityOutcome.Denied);
            if (response.StatusCode != HttpStatusCode.OK || response.Headers.Location is not null ||
                response.Content.Headers.ContentType?.MediaType != "application/json" ||
                response.Content.Headers.ContentLength > MaximumPayloadBytes) return Unavailable();

            await using var source = await response.Content.ReadAsStreamAsync(boundedTime.Token);
            using var body = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await source.ReadAsync(buffer, boundedTime.Token)) != 0)
            {
                if (body.Length + count > MaximumPayloadBytes) return Unavailable();
                body.Write(buffer, 0, count);
            }
            boundedTime.Token.ThrowIfCancellationRequested();
            using var json = JsonDocument.Parse(body.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || HasDuplicateProperties(root) ||
                !root.TryGetProperty(CanonicalCustomerOwnerSourceContract.IdentityProperty, out var identity) ||
                identity.ValueKind != JsonValueKind.Number || !identity.TryGetInt32(out var id) || id <= 0 || id != customerId)
                return Unavailable();
            return new(DocumentAuthorityOutcome.Allowed, new CanonicalDocumentCustomer(id));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or InvalidOperationException or
            ArgumentException or FormatException or OperationCanceledException or TimeoutException)
        {
            return Unavailable();
        }
    }

    private static bool HasDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
                if (!names.Add(property.Name) || HasDuplicateProperties(property.Value)) return true;
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray())
                if (HasDuplicateProperties(item)) return true;
        return false;
    }

    private static CustomerDocumentOwnerRead<CanonicalDocumentCustomer> Unavailable() => new(DocumentAuthorityOutcome.Unavailable);
}
