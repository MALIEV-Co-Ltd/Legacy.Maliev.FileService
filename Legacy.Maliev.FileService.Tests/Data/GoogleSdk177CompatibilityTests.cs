using System.Security.Cryptography;
using System.Net.Http.Headers;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Requests;
using Google.Apis.Services;
using Google.Apis.Storage.v1;
using Google.Apis.Util;
using Google.Cloud.Storage.V1;
using Legacy.Maliev.FileService.Data;

namespace Legacy.Maliev.FileService.Tests.Data;

// No network execution, ADC, persisted keys or cloud resources. Real installed
// Google SDK request construction and an ephemeral local RSA signing credential.
public sealed class GoogleSdk177CompatibilityTests
{
    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public void SingleSegmentDotTraversalIsRejected(string value)
    {
        using var service = new StorageService(new BaseClientService.Initializer());
        var request = service.Objects.Get("synthetic-private", value);
        Assert.Throws<ArgumentException>(() => request.CreateRequest());
    }

    [Theory]
    [InlineData("a/../b")]
    [InlineData("a/./b")]
    public void ReservedExpansionDotSegmentsAreRejected(string value)
    {
        var builder = new RequestBuilder
        {
            BaseUri = new Uri("https://synthetic.example.invalid/"),
            Path = "v1/{+name}",
        };
        builder.AddParameter(RequestParameterType.Path, "name", value);
        Assert.Throws<ArgumentException>(() => builder.BuildUri());
    }

    [Theory]
    [InlineData("orders/ค่าทำสี.stl")]
    [InlineData("orders/a?generation=9#fragment.stl")]
    [InlineData("orders/a%2F..%2Fb.stl")]
    [InlineData("orders/a+b&c.stl")]
    public void GeneratedStorageDeleteKeepsObjectAndGenerationParametersSeparate(string value)
    {
        using var service = new StorageService(new BaseClientService.Initializer());
        var request = service.Objects.Delete("synthetic-private", value);
        request.Generation = 17;
        request.IfGenerationMatch = 17;
        using var http = request.CreateRequest();
        var uri = http.RequestUri!;
        Assert.Equal(HttpMethod.Delete, http.Method);
        Assert.Equal("storage.googleapis.com", uri.Host);
        Assert.Equal("/storage/v1/b/synthetic-private/o/" + Uri.EscapeDataString(value), uri.AbsolutePath);
        Assert.Equal(string.Empty, uri.Fragment);
        var query = Query(uri);
        Assert.Equal("17", query["generation"]);
        Assert.Equal("17", query["ifGenerationMatch"]);
        Assert.DoesNotContain("9", query.Values);
    }

    [Theory]
    [InlineData(1, 3600)]
    [InlineData(720, 604800)]
    public async Task AdapterSignsRealV4UrlWithEphemeralRsaAndBoundedExpiry(int hours, int seconds)
    {
        using var rsa = RSA.Create(2048);
        var credential = new ServiceAccountCredential(new ServiceAccountCredential.Initializer("synthetic@example.invalid")
            .FromPrivateKey(rsa.ExportPkcs8PrivateKeyPem()));
        var adapter = new GoogleCloudObjectStorage(null!, UrlSigner.FromCredential(credential));
        var uri = await adapter.CreateSignedReadUriAsync("synthetic-private", "orders/ค่าทำสี.stl", TimeSpan.FromHours(hours), default);
        var query = Query(uri);
        Assert.Equal("GOOG4-RSA-SHA256", query["X-Goog-Algorithm"]);
        Assert.Equal(seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), query["X-Goog-Expires"]);
        Assert.StartsWith("synthetic@example.invalid/", query["X-Goog-Credential"], StringComparison.Ordinal);
        Assert.Equal(512, query["X-Goog-Signature"].Length);
        Assert.True(ContentDispositionHeaderValue.TryParse(query["response-content-disposition"], out var disposition));
        Assert.Equal("ค่าทำสี.stl", disposition.FileNameStar);
        Assert.Equal(string.Empty, uri.Fragment);
    }

    private static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?').Split('&')
        .Select(value => value.Split('=', 2)).ToDictionary(value => Uri.UnescapeDataString(value[0]),
            value => Uri.UnescapeDataString(value[1]), StringComparer.Ordinal);
}
