using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Legacy.Maliev.FileService.Application.CustomerDocuments;
using Legacy.Maliev.FileService.Data.CustomerDocuments;

namespace Legacy.Maliev.FileService.Tests.CustomerDocuments;

// Controlled wire tests prove parsing/transport only, never Auth-to-Employee binding, deployed HR authority, or current-session eligibility.
public sealed class CustomerDocumentEmploymentHttpReaderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    private const string Audited = "{\"EmployeeId\":23,\"State\":\"active\",\"Revision\":7,\"EffectiveAtUtc\":\"2026-10-07T09:00:00Z\",\"VerifiedAtUtc\":\"2026-10-07T10:00:00Z\",\"VerifiedBySubject\":\"synthetic-hr\"}";

    [Theory]
    [InlineData("active", DocumentEmployeeEmploymentState.Active, true)]
    [InlineData("inactive", DocumentEmployeeEmploymentState.Inactive, false)]
    [InlineData("unknown", DocumentEmployeeEmploymentState.Unknown, false)]
    public async Task ExactAuditedProjectionPreservesStateAndCanonicalIdentity(string state, DocumentEmployeeEmploymentState expected, bool eligible)
    {
        using var factory = Fixture((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/employees/23/employment", request.RequestUri!.PathAndQuery);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("synthetic-server-credential", request.Headers.Authorization?.Parameter);
            Assert.Contains(request.Headers.Accept, value => value.MediaType == "application/json");
            return Task.FromResult(Json(Audited.Replace("\"active\"", "\"" + state + "\"")));
        });
        var result = await Reader(factory).ReadAsync(23, default);
        Assert.Equal(DocumentAuthorityOutcome.Allowed, result.Outcome);
        Assert.NotNull(result.Value);
        Assert.Equal(23, result.Value.EmployeeId);
        Assert.Equal(expected, result.Value.State);
        Assert.Equal(eligible, result.Value.IsActiveEmployment);
        Assert.Equal(7, result.Value.Revision);
        Assert.Equal(new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero), result.Value.EffectiveAtUtc);
        Assert.Equal(new DateTimeOffset(2026, 10, 7, 10, 0, 0, TimeSpan.Zero), result.Value.VerifiedAtUtc);
        Assert.Equal("synthetic-hr", result.Value.VerifiedBySubject);
    }

    [Theory]
    [InlineData("{\"EmployeeId\":23,\"State\":\"unknown\",\"Revision\":0}")]
    [InlineData("{\"EmployeeId\":23,\"State\":\"unknown\",\"Revision\":0,\"EffectiveAtUtc\":null,\"VerifiedAtUtc\":null,\"VerifiedBySubject\":null}")]
    public async Task InitialUnknownWithOmittedOrNullAuditRemainsDistinctFromUnavailable(string body)
    {
        using var factory = Fixture((_, _) => Task.FromResult(Json(body)));
        var result = await Reader(factory).ReadAsync(23, default);
        Assert.Equal(DocumentAuthorityOutcome.Allowed, result.Outcome);
        Assert.NotNull(result.Value);
        Assert.Equal(DocumentEmployeeEmploymentState.Unknown, result.Value.State);
        Assert.Equal(0, result.Value.Revision);
        Assert.False(result.Value.IsActiveEmployment);
        Assert.Null(result.Value.EffectiveAtUtc);
        Assert.Null(result.Value.VerifiedAtUtc);
        Assert.Null(result.Value.VerifiedBySubject);
    }

    [Theory]
    [InlineData("identityMismatch")]
    [InlineData("identityZero")]
    [InlineData("identityString")]
    [InlineData("identityNull")]
    [InlineData("identityOverflow")]
    [InlineData("identityFraction")]
    [InlineData("identityMissing")]
    [InlineData("identityCamel")]
    [InlineData("stateOrdinal")]
    [InlineData("stateCase")]
    [InlineData("stateNull")]
    [InlineData("stateMissing")]
    [InlineData("stateOther")]
    [InlineData("revisionMissing")]
    [InlineData("revisionNegative")]
    [InlineData("revisionNull")]
    [InlineData("revisionString")]
    [InlineData("revisionFraction")]
    [InlineData("activeZero")]
    [InlineData("inactiveZero")]
    [InlineData("unknownZeroAudited")]
    [InlineData("effectiveMissing")]
    [InlineData("effectiveNull")]
    [InlineData("effectiveNonUtc")]
    [InlineData("effectiveNoZone")]
    [InlineData("effectiveDefault")]
    [InlineData("effectiveAfterVerified")]
    [InlineData("verifiedMissing")]
    [InlineData("verifiedNull")]
    [InlineData("verifiedNonUtc")]
    [InlineData("verifiedNoZone")]
    [InlineData("verifiedDefault")]
    [InlineData("verifiedFuture")]
    [InlineData("verifiedInvalidDate")]
    [InlineData("actorMissing")]
    [InlineData("actorNull")]
    [InlineData("actorNumber")]
    [InlineData("actorBlank")]
    [InlineData("actorOversize")]
    [InlineData("unknownPositiveWithoutAudit")]
    public async Task InvalidExactWireProjectionCannotProduceEmploymentEligibility(string fault)
    {
        var body = JsonNode.Parse(Audited)!.AsObject();
        switch (fault)
        {
            case "identityMismatch": body["EmployeeId"] = 24; break;
            case "identityZero": body["EmployeeId"] = 0; break;
            case "identityString": body["EmployeeId"] = "23"; break;
            case "identityNull": body["EmployeeId"] = null; break;
            case "identityOverflow": body["EmployeeId"] = 2147483648L; break;
            case "identityFraction": body["EmployeeId"] = 23.5; break;
            case "identityMissing": body.Remove("EmployeeId"); break;
            case "identityCamel": body.Remove("EmployeeId"); body["employeeId"] = 23; break;
            case "stateOrdinal": body["State"] = 1; break;
            case "stateCase": body["State"] = "Active"; break;
            case "stateNull": body["State"] = null; break;
            case "stateMissing": body.Remove("State"); break;
            case "stateOther": body["State"] = "employed"; break;
            case "revisionMissing": body.Remove("Revision"); break;
            case "revisionNegative": body["Revision"] = -1; break;
            case "revisionNull": body["Revision"] = null; break;
            case "revisionString": body["Revision"] = "7"; break;
            case "revisionFraction": body["Revision"] = 7.5; break;
            case "activeZero": body["Revision"] = 0; break;
            case "inactiveZero": body["Revision"] = 0; body["State"] = "inactive"; break;
            case "unknownZeroAudited": body["Revision"] = 0; body["State"] = "unknown"; break;
            case "effectiveMissing": body.Remove("EffectiveAtUtc"); break;
            case "effectiveNull": body["EffectiveAtUtc"] = null; break;
            case "effectiveNonUtc": body["EffectiveAtUtc"] = "2026-10-07T09:00:00+07:00"; break;
            case "effectiveNoZone": body["EffectiveAtUtc"] = "2026-10-07T09:00:00"; break;
            case "effectiveDefault": body["EffectiveAtUtc"] = "0001-01-01T00:00:00Z"; break;
            case "effectiveAfterVerified": body["EffectiveAtUtc"] = "2026-10-07T11:00:00Z"; break;
            case "verifiedMissing": body.Remove("VerifiedAtUtc"); break;
            case "verifiedNull": body["VerifiedAtUtc"] = null; break;
            case "verifiedNonUtc": body["VerifiedAtUtc"] = "2026-10-07T10:00:00+07:00"; break;
            case "verifiedNoZone": body["VerifiedAtUtc"] = "2026-10-07T10:00:00"; break;
            case "verifiedDefault": body["VerifiedAtUtc"] = "0001-01-01T00:00:00Z"; break;
            case "verifiedFuture": body["VerifiedAtUtc"] = "2026-10-09T10:00:00Z"; break;
            case "verifiedInvalidDate": body["VerifiedAtUtc"] = "yesterday"; break;
            case "actorMissing": body.Remove("VerifiedBySubject"); break;
            case "actorNull": body["VerifiedBySubject"] = null; break;
            case "actorNumber": body["VerifiedBySubject"] = 23; break;
            case "actorBlank": body["VerifiedBySubject"] = "   "; break;
            case "actorOversize": body["VerifiedBySubject"] = new string('x', 257); break;
            case "unknownPositiveWithoutAudit": body["State"] = "unknown"; body.Remove("EffectiveAtUtc"); body.Remove("VerifiedAtUtc"); body.Remove("VerifiedBySubject"); break;
        }
        using var factory = Fixture((_, _) => Task.FromResult(Json(body.ToJsonString())));
        await AssertUnavailable(Reader(factory));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{broken")]
    [InlineData("{\"EmployeeId\":23,\"EmployeeId\":24,\"State\":\"unknown\",\"Revision\":0}")]
    [InlineData("{\"EmployeeId\":23,\"employeeId\":23,\"State\":\"unknown\",\"Revision\":0}")]
    [InlineData("{\"EmployeeId\":23,\"State\":\"unknown\",\"State\":\"active\",\"Revision\":0}")]
    [InlineData("{\"EmployeeId\":23,\"State\":\"unknown\",\"Revision\":0,\"Extra\":{\"X\":1,\"x\":2}}")]
    [InlineData("{\"EmployeeId\":23,\"State\":\"unknown\",\"Revision\":0,\"verifiedAtUtc\":null}")]
    public async Task MalformedOrDuplicatePropertiesCannotBecomeEvidence(string body)
    {
        using var factory = Fixture((_, _) => Task.FromResult(Json(body)));
        await AssertUnavailable(Reader(factory));
    }

    [Theory]
    [InlineData(401, DocumentAuthorityOutcome.Denied)]
    [InlineData(403, DocumentAuthorityOutcome.Denied)]
    [InlineData(404, DocumentAuthorityOutcome.Denied)]
    [InlineData(302, DocumentAuthorityOutcome.Unavailable)]
    [InlineData(500, DocumentAuthorityOutcome.Unavailable)]
    [InlineData(503, DocumentAuthorityOutcome.Unavailable)]
    public async Task RefusedOrUnavailableOwnerNeverProducesState(int status, DocumentAuthorityOutcome expected)
    {
        using var factory = Fixture((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)));
        var result = await Reader(factory).ReadAsync(23, default);
        Assert.Equal(expected, result.Outcome);
        Assert.Null(result.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("oversize")]
    [InlineData("line\r\nbreak")]
    public async Task MissingOrInvalidServerTokenNeverMakesOwnerRequest(string? accessToken)
    {
        var calls = 0;
        using var factory = Fixture((_, _) => { calls++; return Task.FromResult(Json(Audited)); });
        var token = accessToken == "oversize" ? new string('a', 16385) : accessToken;
        await AssertUnavailable(new(factory, new Credential(token), timeProvider: new Clock(Now)));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task AbsentCredentialNeverMakesOwnerRequest()
    {
        using var factory = Fixture((_, _) => throw new InvalidOperationException("No owner request permitted"));
        await AssertUnavailable(new(factory));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task NonpositiveCanonicalIdentityNeverMakesOwnerRequest(int employeeId)
    {
        using var factory = Fixture((_, _) => throw new InvalidOperationException("No owner request permitted"));
        var result = await Reader(factory).ReadAsync(employeeId, default);
        Assert.Equal(DocumentAuthorityOutcome.Unavailable, result.Outcome);
        Assert.Null(result.Value);
    }

    [Theory]
    [InlineData("media")]
    [InlineData("redirect")]
    [InlineData("origin")]
    public async Task UnsafeTransportRefusesProjection(string fault)
    {
        using var factory = Fixture((request, _) =>
        {
            var response = Json(Audited);
            response.RequestMessage = request;
            if (fault == "media") response.Content.Headers.ContentType = new("text/plain");
            if (fault == "redirect") response.Headers.Location = new("https://other.example.invalid/");
            if (fault == "origin") response.RequestMessage = new(HttpMethod.Get, "https://other.example.invalid/employees/23/employment");
            return Task.FromResult(response);
        });
        await AssertUnavailable(Reader(factory));
    }

    [Theory]
    [InlineData(65536, DocumentAuthorityOutcome.Allowed)]
    [InlineData(65537, DocumentAuthorityOutcome.Unavailable)]
    public async Task UnadvertisedFullBodySizeHasInclusive64KiBBound(int bytes, DocumentAuthorityOutcome expected)
    {
        const string prefix = "{\"EmployeeId\":23,\"State\":\"unknown\",\"Revision\":0,\"Padding\":\"";
        const string suffix = "\"}";
        var payload = Encoding.UTF8.GetBytes(prefix + new string('x', bytes - prefix.Length - suffix.Length) + suffix);
        using var factory = Fixture((_, _) =>
        {
            var content = new UnknownLengthContent(payload);
            content.Headers.ContentType = new("application/json");
            Assert.Null(content.Headers.ContentLength);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        Assert.Equal(expected, (await Reader(factory).ReadAsync(23, default)).Outcome);
    }

    [Fact]
    public async Task TimeoutReturnsUnavailableAndCallerCancellationPropagates()
    {
        using var factory = Fixture(async (_, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return Json(Audited); });
        var reader = new CustomerDocumentEmploymentHttpReader(factory, new Credential("synthetic-server-credential"), TimeSpan.FromMilliseconds(20), new Clock(Now));
        await AssertUnavailable(reader);
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(23, caller.Token));
        Assert.Equal(caller.Token, error.CancellationToken);
    }

    [Theory]
    [InlineData("http://employee.example.invalid/")]
    [InlineData("https://user:password@employee.example.invalid/")]
    [InlineData("https://employee.example.invalid/base/")]
    [InlineData("https://employee.example.invalid/?query=1")]
    [InlineData("https://employee.example.invalid/#fragment")]
    public void ProductionOriginRequiresExplicitHttpsRoot(string origin) =>
        Assert.Throws<ArgumentException>(() => new CustomerDocumentEmploymentHttpClientFactory(new Uri(origin)));

    private static CustomerDocumentEmploymentHttpReader Reader(CustomerDocumentEmploymentHttpClientFactory factory) =>
        new(factory, new Credential("synthetic-server-credential"), timeProvider: new Clock(Now));
    private static async Task AssertUnavailable(CustomerDocumentEmploymentHttpReader reader)
    {
        var result = await reader.ReadAsync(23, default);
        Assert.Equal(DocumentAuthorityOutcome.Unavailable, result.Outcome);
        Assert.Null(result.Value);
    }
    private static CustomerDocumentEmploymentHttpClientFactory Fixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) =>
        CustomerDocumentEmploymentHttpClientFactory.CreateForIsolatedLoopbackTests(new("http://127.0.0.1:32192/"), new Handler(respond));
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class Clock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    private sealed class Credential(string? value) : ICustomerDocumentOwnerCredential
    {
        public Task<string?> GetAccessTokenAsync(Uri ownerOrigin, CancellationToken token)
        {
            Assert.Equal(new Uri("http://127.0.0.1:32192/"), ownerOrigin);
            return Task.FromResult(value);
        }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => respond(request, token);
    }
    private sealed class UnknownLengthContent(byte[] payload) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(payload).AsTask();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(payload, writable: false));
    }
}
