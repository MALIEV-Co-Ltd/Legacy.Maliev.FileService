using System.Security.Cryptography;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Http;
using Google.Cloud.Storage.V1;

namespace Legacy.Maliev.FileService.Data;

/// <summary>Owns an ephemeral local SDK signing key; no ADC, key files or signing HTTP calls.</summary>
public sealed class HostedAcceptanceSigningIdentity : IDisposable
{
    private readonly RSA key = RSA.Create(2048);

    /// <summary>Creates a genuine SDK signer using only the owned in-memory RSA key.</summary>
    public HostedAcceptanceSigningIdentity()
    {
        try
        {
            Signer = UrlSigner.FromCredential(new ServiceAccountCredential(new ServiceAccountCredential.Initializer(
                "hosted-file-signing@example.invalid") { Key = key, HttpClientFactory = new RejectingFactory() }));
            VerificationPublicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    /// <summary>Gets the actual Google SDK signer shared within this admitted host.</summary>
    public UrlSigner Signer { get; }
    /// <summary>Gets only the public verification key for the admitted endpoint's V4 verifier.</summary>
    public string VerificationPublicKey { get; }
    /// <inheritdoc />
    public void Dispose() => key.Dispose();

    private sealed class RejectingFactory : Google.Apis.Http.HttpClientFactory
    {
        protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args) => new RejectingHandler();
        private sealed class RejectingHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromException<HttpResponseMessage>(new InvalidOperationException("Hosted signer network access is forbidden."));
        }
    }
}
