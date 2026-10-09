using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.FileService.Api.CustomerDocuments;
using Maliev.Aspire.ServiceDefaults.LegacyAuth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Tokens;
using Moq;
namespace Legacy.Maliev.FileService.Tests.CustomerDocuments;

// Ephemeral isolated signing keys, synthetic issuers and claims. No production key or authentication binding.
public sealed class DocumentAssociationOwnerCredentialSecurityTests
{
    [Theory]
    [InlineData("issuer")]
    [InlineData("kid")]
    [InlineData("duplicate-kind")]
    [InlineData("client")]
    [InlineData("stale")]
    [InlineData("impersonation")]
    [InlineData("signature")]
    [InlineData("user-id")]
    [InlineData("nameidentifier")]
    [InlineData("nameidentifier-uri")]
    public async Task Rejected_provider_JWT_never_becomes_owner_credential(string fault)
    {
        using var signer = RSA.Create(2048);
        using var otherSigner = RSA.Create(2048);
        var now = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        var issued = now.ToUnixTimeSeconds() - 60;
        var header = new Dictionary<string, object> { ["alg"] = "RS256", ["kid"] = fault == "kid" ? "unknown-key" : "isolated-rsa-key", ["typ"] = "JWT" };
        var payload = Payload(issued, now.ToUnixTimeSeconds() + 600);
        if (fault == "issuer") payload["iss"] = "https://other-issuer.invalid/";
        if (fault == "stale") payload["exp"] = now.ToUnixTimeSeconds() - 1;
        if (fault == "impersonation") payload["employee_id"] = "synthetic-employee";
        if (fault == "user-id") payload["user_id"] = "synthetic-delegated-user";
        if (fault == "nameidentifier") payload["nameidentifier"] = "synthetic-delegated-user";
        if (fault == "nameidentifier-uri") payload[System.Security.Claims.ClaimTypes.NameIdentifier] = "synthetic-delegated-user";
        var body = JsonSerializer.Serialize(payload);
        if (fault == "duplicate-kind") body = body[..^1] + ",\"identity_kind\":\"service\"}";
        var token = Sign(header, body, fault == "signature" ? otherSigner : signer);
        var provider = new ControlledProvider(token);
        var bridge = Bridge(signer, provider, now, fault == "client" ? "legacy-web" : "legacy-file");
        Assert.Null(await bridge.GetAccessTokenAsync(new("https://order.invalid/"), CancellationToken.None));
    }

    [Fact]
    public async Task Only_signed_exact_own_service_identity_with_bounded_UTC_lifetime_is_accepted()
    {
        using var signer = RSA.Create(2048);
        var now = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        var token = Sign(new() { ["alg"] = "RS256", ["kid"] = "isolated-rsa-key", ["typ"] = "JWT" },
            JsonSerializer.Serialize(Payload(now.ToUnixTimeSeconds() - 60, now.ToUnixTimeSeconds() + 600)), signer);
        var provider = new ControlledProvider(token);
        Assert.Equal(token, await Bridge(signer, provider, now, "legacy-file").GetAccessTokenAsync(new("https://order.invalid/"), CancellationToken.None));
    }
    private static CustomerDocumentOwnerCredential Bridge(RSA signer, ControlledProvider provider, DateTimeOffset now, string clientId)
    {
        var options = new JwtBearerOptions
        {
            TokenValidationParameters = new()
            {
                ValidateIssuer = true,
                ValidIssuer = "https://isolated-issuer.invalid/",
                ValidateAudience = true,
                ValidAudience = "isolated-owner-audience",
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new RsaSecurityKey(signer) { KeyId = "isolated-rsa-key" },
                RequireSignedTokens = true,
                RequireExpirationTime = true,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero
            }
        };
        var monitor = new Mock<IOptionsMonitor<JwtBearerOptions>>();
        monitor.Setup(value => value.Get(JwtBearerDefaults.AuthenticationScheme)).Returns(options);
        return new(provider, [new("https://order.invalid/")], monitor.Object,
            Options.Create(new LegacyServiceAuthenticationOptions { ClientId = clientId }), new FakeTimeProvider(now));
    }
    private static Dictionary<string, object> Payload(long issued, long expires) => new()
    {
        ["iss"] = "https://isolated-issuer.invalid/",
        ["aud"] = "isolated-owner-audience",
        ["sub"] = "service:legacy-file",
        ["identity_kind"] = "service",
        ["name"] = "legacy-file",
        ["iat"] = issued,
        ["exp"] = expires
    };
    private static string Sign(Dictionary<string, object> header, string body, RSA signer)
    {
        var value = Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(header))) + "." +
            Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(body));
        return value + "." + Base64UrlEncoder.Encode(signer.SignData(Encoding.ASCII.GetBytes(value), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }
    private sealed class ControlledProvider(string value) : ILegacyServiceAccessTokenProvider
    {
        public ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<string?>(value);
        public void Invalidate(string token) { }
    }
}
