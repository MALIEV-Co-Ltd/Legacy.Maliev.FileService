using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Legacy.Maliev.FileService.Application.CustomerDocuments;

namespace Legacy.Maliev.FileService.Data.CustomerDocuments;

/// <summary>Creates bounded owner clients with redirects and ambient cookies disabled.</summary>
public sealed class CustomerDocumentOwnerHttpClientFactory : IDisposable
{
    private readonly HttpClient client;
    /// <summary>Gets the configured order origin.</summary>
    public Uri OrderOrigin { get; }
    /// <summary>Gets the configured quotation origin.</summary>
    public Uri QuotationOrigin { get; }
    /// <summary>Creates production clients for explicit HTTPS origins only.</summary>
    public CustomerDocumentOwnerHttpClientFactory(Uri orderOrigin, Uri quotationOrigin)
        : this(orderOrigin, quotationOrigin, new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }, false) { }
    private CustomerDocumentOwnerHttpClientFactory(Uri orderOrigin, Uri quotationOrigin, HttpMessageHandler handler, bool isolatedLoopback)
    {
        OrderOrigin = ValidateOrigin(orderOrigin, isolatedLoopback);
        QuotationOrigin = ValidateOrigin(quotationOrigin, isolatedLoopback);
        client = new HttpClient(handler, true) { Timeout = Timeout.InfiniteTimeSpan };
    }
    /// <summary>Creates an explicitly isolated loopback transport fixture; never use for production registration.</summary>
    public static CustomerDocumentOwnerHttpClientFactory CreateForIsolatedLoopbackTests(Uri origin, HttpMessageHandler controlledHandler) =>
        new(origin, origin, controlledHandler, true);
    internal Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
        client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
    private static Uri ValidateOrigin(Uri origin, bool isolatedLoopback)
    {
        if (!origin.IsAbsoluteUri || origin.UserInfo.Length != 0 || origin.Query.Length != 0 || origin.Fragment.Length != 0 ||
            origin.AbsolutePath != "/" || (origin.Scheme != Uri.UriSchemeHttps &&
            !(isolatedLoopback && origin.IsLoopback && origin.Scheme == Uri.UriSchemeHttp)))
            throw new ArgumentException("An explicit HTTPS owner origin is required.", nameof(origin));
        if (isolatedLoopback && !origin.IsLoopback) throw new ArgumentException("The fixture must use loopback.", nameof(origin));
        return origin;
    }
    /// <summary>Disposes owned HTTP resources.</summary>
    public void Dispose() => client.Dispose();
}

/// <summary>Reads pinned Order cb57f8f and Quotation 438b8da owner wire contracts with no local ownership override.</summary>
public sealed class CustomerDocumentOwnerHttpReads(
    CustomerDocumentOwnerHttpClientFactory factory,
    ICanonicalDocumentCustomerReader? customers = null,
    ICustomerDocumentOwnerCredential? credential = null) : ICustomerDocumentOwnerReads
{
    private const int MaximumPayloadBytes = 65536;
    // Owner authority remains at the existing JWT/IAM middleware; this client grants no permissions.
    /// <summary>Requires the separately accepted CRM existence contract.</summary>
    public Task<CustomerDocumentOwnerRead<CanonicalDocumentCustomer>> ReadCustomerAsync(int customerId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return customers is null
            ? Task.FromResult(Unavailable<CanonicalDocumentCustomer>())
            : customers.ReadAsync(customerId, token);
    }
    /// <summary>Reads PascalCase Id and required CustomerId; no quotation identity is inferred.</summary>
    public Task<CustomerDocumentOwnerRead<CanonicalDocumentOrder>> ReadOrderAsync(int orderId, CancellationToken token) =>
        ReadAsync(factory.OrderOrigin, $"orders/{orderId}", root =>
        {
            var id = PositiveId(root, "Id");
            var customer = PositiveId(root, "CustomerId");
            return id == orderId ? new CanonicalDocumentOrder(id, customer) : null;
        }, token);
    /// <summary>Reads CustomerQuotationDetails.Quotation and requires exact requested identities.</summary>
    public Task<CustomerDocumentOwnerRead<CanonicalDocumentQuotation>> ReadQuotationAsync(int quotationId, int customerId, CancellationToken token) =>
        ReadAsync(factory.QuotationOrigin, $"Quotations/{quotationId}?customerId={customerId}", root =>
        {
            var quotation = root.GetProperty("Quotation");
            var id = PositiveId(quotation, "Id");
            var customer = PositiveId(quotation, "CustomerId");
            return id == quotationId && customer == customerId ? new CanonicalDocumentQuotation(id, customer) : null;
        }, token);
    /// <summary>Reads current quotation-order links; absent/empty links refuse the association.</summary>
    public Task<CustomerDocumentOwnerRead<IReadOnlyList<CanonicalDocumentOrderLink>>> ReadQuotationOrdersAsync(int quotationId, CancellationToken token) =>
        ReadAsync<IReadOnlyList<CanonicalDocumentOrderLink>>(factory.QuotationOrigin, $"quotations/{quotationId}/orders", root =>
        {
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0 || root.GetArrayLength() > 512) return null;
            var links = root.EnumerateArray().Select(item => new CanonicalDocumentOrderLink(
                PositiveId(item, "Id"), PositiveId(item, "QuotationId"), PositiveId(item, "OrderId"))).ToArray();
            return links.Any(link => link.QuotationId != quotationId) ||
                links.Select(link => link.Id).Distinct().Count() != links.Length ||
                links.Select(link => link.OrderId).Distinct().Count() != links.Length ? null : links;
        }, token);
    private async Task<CustomerDocumentOwnerRead<T>> ReadAsync<T>(Uri origin, string relativePath, Func<JsonElement, T?> parse, CancellationToken token) where T : class
    {
        token.ThrowIfCancellationRequested();
        if (credential is null) return Unavailable<T>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var accessToken = await credential.GetAccessTokenAsync(origin, timeout.Token);
            if (string.IsNullOrWhiteSpace(accessToken) || accessToken.Length > 16384) return Unavailable<T>();
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(origin, relativePath));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await factory.SendAsync(request, timeout.Token);
            if (response.RequestMessage?.RequestUri is { } actual && actual != request.RequestUri) return Unavailable<T>();
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                return new(DocumentAuthorityOutcome.Denied);
            if (response.StatusCode != HttpStatusCode.OK || response.Headers.Location is not null) return Unavailable<T>();
            var media = response.Content.Headers.ContentType?.MediaType;
            if (media != "application/json" || response.Content.Headers.ContentLength > MaximumPayloadBytes) return Unavailable<T>();
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var bounded = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(buffer, timeout.Token)) != 0)
            {
                if (bounded.Length + count > MaximumPayloadBytes) return Unavailable<T>();
                bounded.Write(buffer, 0, count);
            }
            using var json = JsonDocument.Parse(bounded.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
            if (HasDuplicateProperties(json.RootElement)) return Unavailable<T>();
            var value = parse(json.RootElement);
            return value is null ? new(DocumentAuthorityOutcome.Denied) : new(DocumentAuthorityOutcome.Allowed, value);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or InvalidOperationException or
            KeyNotFoundException or FormatException or OperationCanceledException or ArgumentException)
        {
            return Unavailable<T>();
        }
    }
    private static int PositiveId(JsonElement root, string name)
    {
        var value = root.GetProperty(name).GetInt32();
        if (value <= 0) throw new JsonException("Invalid owner identity.");
        return value;
    }
    private static bool HasDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
                if (!seen.Add(property.Name) || HasDuplicateProperties(property.Value)) return true;
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) if (HasDuplicateProperties(item)) return true;
        return false;
    }
    private static CustomerDocumentOwnerRead<T> Unavailable<T>() => new(DocumentAuthorityOutcome.Unavailable);
}
