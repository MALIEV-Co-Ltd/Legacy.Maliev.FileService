using Legacy.Maliev.FileService.Application.CustomerDocuments;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
namespace Legacy.Maliev.FileService.Data.CustomerDocuments;
/// <summary>Reads AllowSocialMedia from the pinned Order cb57f8f GET order contract.</summary>
public sealed class NdaOrderConsentHttpReader(CustomerDocumentOwnerHttpClientFactory? factory = null, ICustomerDocumentOwnerCredential? credential = null) : INdaOrderConsentReader
{
    /// <summary>Requires exact owner/customer identity and a current server credential.</summary>
    public async Task<CustomerDocumentOwnerRead<bool>> ReadAsync(int orderId, int customerId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (orderId <= 0 || customerId <= 0) return new(DocumentAuthorityOutcome.Denied);
        if (factory is null || credential is null) return new(DocumentAuthorityOutcome.Unavailable);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var accessToken = await credential.GetAccessTokenAsync(factory.OrderOrigin, timeout.Token);
            if (string.IsNullOrWhiteSpace(accessToken) || accessToken.Length > 16384) return new(DocumentAuthorityOutcome.Unavailable);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(factory.OrderOrigin, $"orders/{orderId}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await factory.SendAsync(request, timeout.Token);
            if (response.RequestMessage?.RequestUri is { } actual && actual != request.RequestUri) return new(DocumentAuthorityOutcome.Unavailable);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                return new(DocumentAuthorityOutcome.Denied);
            if (response.StatusCode != HttpStatusCode.OK || response.Headers.Location is not null ||
                response.Content.Headers.ContentType?.MediaType != "application/json" || response.Content.Headers.ContentLength > 65536)
                return new(DocumentAuthorityOutcome.Unavailable);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var bounded = new MemoryStream();
            var bytes = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(bytes, timeout.Token)) != 0)
            {
                if (bounded.Length + count > 65536) return new(DocumentAuthorityOutcome.Unavailable);
                bounded.Write(bytes, 0, count);
            }
            using var json = JsonDocument.Parse(bounded.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Duplicate(root) ||
                !root.TryGetProperty("Id", out var id) || !id.TryGetInt32(out var returnedId) || returnedId <= 0 ||
                !root.TryGetProperty("CustomerId", out var customer) || !customer.TryGetInt32(out var returnedCustomer) || returnedCustomer <= 0 ||
                !root.TryGetProperty("AllowSocialMedia", out var consent) || consent.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return new(DocumentAuthorityOutcome.Unavailable);
            if (returnedId != orderId || returnedCustomer != customerId) return new(DocumentAuthorityOutcome.Denied);
            return new(DocumentAuthorityOutcome.Allowed, consent.GetBoolean());
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or InvalidOperationException or
            KeyNotFoundException or FormatException or OperationCanceledException or ArgumentException)
        {
            return new(DocumentAuthorityOutcome.Unavailable);
        }
    }
    private static bool Duplicate(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
                if (!names.Add(property.Name) || Duplicate(property.Value)) return true;
        }
        if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) if (Duplicate(item)) return true;
        return false;
    }
}
