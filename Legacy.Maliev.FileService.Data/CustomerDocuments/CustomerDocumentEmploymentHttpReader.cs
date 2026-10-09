using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
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


/// <summary>Consumes only the exact employee HR projection; never supplies Auth identity binding or file permission.</summary>
public sealed class CustomerDocumentEmploymentHttpReader : ICustomerDocumentEmploymentReader
{
    private const int MaximumPayloadBytes = 65536;
    private readonly CustomerDocumentEmploymentHttpClientFactory factory;
    private readonly ICustomerDocumentOwnerCredential? credential;
    private readonly TimeSpan timeout;
    private readonly TimeProvider clock;
    /// <summary>Creates a separate Employee-origin read using an optional existing server credential and bounded timeout.</summary>
    public CustomerDocumentEmploymentHttpReader(CustomerDocumentEmploymentHttpClientFactory factory,
        ICustomerDocumentOwnerCredential? credential = null, TimeSpan? timeout = null, TimeProvider? timeProvider = null)
    {
        this.factory = factory;
        this.credential = credential;
        this.timeout = timeout ?? TimeSpan.FromSeconds(10);
        clock = timeProvider ?? TimeProvider.System;
        if (this.timeout <= TimeSpan.Zero || this.timeout > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(timeout));
    }
    /// <inheritdoc />
    public async Task<CustomerDocumentOwnerRead<DocumentEmployeeEmployment>> ReadAsync(int employeeId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (employeeId <= 0 || credential is null) return Unavailable();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        try
        {
            var accessToken = await credential.GetAccessTokenAsync(factory.Origin, deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(accessToken) || accessToken.Length > 16384 || accessToken.Any(char.IsWhiteSpace) || accessToken.Any(char.IsControl))
                return Unavailable();
            using var request = new HttpRequestMessage(HttpMethod.Get,
                new Uri(factory.Origin, "employees/" + employeeId.ToString(CultureInfo.InvariantCulture) + "/employment"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await factory.SendAsync(request, deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            if (response.RequestMessage?.RequestUri is { } actual && actual != request.RequestUri) return Unavailable();
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                return new(DocumentAuthorityOutcome.Denied);
            if (response.StatusCode != HttpStatusCode.OK || response.Headers.Location is not null ||
                response.Content.Headers.ContentType?.MediaType != "application/json" || response.Content.Headers.ContentLength > MaximumPayloadBytes)
                return Unavailable();

            await using var source = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var body = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await source.ReadAsync(buffer, deadline.Token)) != 0)
            {
                if (body.Length + count > MaximumPayloadBytes) return Unavailable();
                body.Write(buffer, 0, count);
            }
            deadline.Token.ThrowIfCancellationRequested();
            using var json = JsonDocument.Parse(body.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
            var projection = Parse(json.RootElement, employeeId, clock.GetUtcNow());
            deadline.Token.ThrowIfCancellationRequested();
            return projection is null ? Unavailable() : new(DocumentAuthorityOutcome.Allowed, projection);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or InvalidOperationException or
            ArgumentException or FormatException or OperationCanceledException or TimeoutException)
        {
            token.ThrowIfCancellationRequested();
            return Unavailable();
        }
    }

    private static DocumentEmployeeEmployment? Parse(JsonElement root, int employeeId, DateTimeOffset now)
    {
        if (root.ValueKind != JsonValueKind.Object || HasDuplicateProperties(root) || HasWrongCaseFields(root) || !FiniteUtc(now) ||
            !root.TryGetProperty("EmployeeId", out var identity) || identity.ValueKind != JsonValueKind.Number ||
            !identity.TryGetInt32(out var id) || id <= 0 || id != employeeId ||
            !root.TryGetProperty("State", out var stateJson) || stateJson.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("Revision", out var revisionJson) || revisionJson.ValueKind != JsonValueKind.Number ||
            !revisionJson.TryGetInt64(out var revision) || revision < 0) return null;
        var state = stateJson.GetString() switch
        {
            "unknown" => DocumentEmployeeEmploymentState.Unknown,
            "active" => DocumentEmployeeEmploymentState.Active,
            "inactive" => DocumentEmployeeEmploymentState.Inactive,
            _ => (DocumentEmployeeEmploymentState)(-1),
        };
        if (!Enum.IsDefined(state)) return null;
        if (revision == 0)
        {
            if (state != DocumentEmployeeEmploymentState.Unknown || !AbsentOrNull(root, "EffectiveAtUtc") ||
                !AbsentOrNull(root, "VerifiedAtUtc") || !AbsentOrNull(root, "VerifiedBySubject")) return null;
            return new(id, state, 0, null, null, null);
        }
        if (!UtcDate(root, "EffectiveAtUtc", out var effective) || !UtcDate(root, "VerifiedAtUtc", out var verified) ||
            effective > verified || verified > now || !root.TryGetProperty("VerifiedBySubject", out var actorJson) ||
            actorJson.ValueKind != JsonValueKind.String) return null;
        var actor = actorJson.GetString();
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 256) return null;
        return new(id, state, revision, effective, verified, actor);
    }

    private static bool AbsentOrNull(JsonElement root, string name) => !root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null;
    private static bool UtcDate(JsonElement root, string name, out DateTimeOffset value)
    {
        value = default;
        if (!root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) return false;
        var text = property.GetString();
        return text is not null && (text.EndsWith('Z') || text.EndsWith("+00:00", StringComparison.Ordinal)) &&
            property.TryGetDateTimeOffset(out value) && FiniteUtc(value);
    }
    private static bool HasWrongCaseFields(JsonElement root)
    {
        string[] fields = ["EmployeeId", "State", "Revision", "EffectiveAtUtc", "VerifiedAtUtc", "VerifiedBySubject"];
        return root.EnumerateObject().Any(property => fields.Any(field =>
            string.Equals(field, property.Name, StringComparison.OrdinalIgnoreCase) && !string.Equals(field, property.Name, StringComparison.Ordinal)));
    }
    private static bool FiniteUtc(DateTimeOffset value) => value != default && value != DateTimeOffset.MaxValue && value.Offset == TimeSpan.Zero;
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
    private static CustomerDocumentOwnerRead<DocumentEmployeeEmployment> Unavailable() => new(DocumentAuthorityOutcome.Unavailable);
}
