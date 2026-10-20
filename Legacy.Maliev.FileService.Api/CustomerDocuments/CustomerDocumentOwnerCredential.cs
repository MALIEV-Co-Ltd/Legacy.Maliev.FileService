using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text.Json;
using Legacy.Maliev.FileService.Application.CustomerDocuments;
using Maliev.Aspire.ServiceDefaults.LegacyAuth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Legacy.Maliev.FileService.Api.CustomerDocuments;

/// <summary>Validates the existing server provider's own workload identity before owner transport can use it.</summary>
/// <remarks>This draft bridge is unregistered pending File/Auth owner acceptance. Existing bearer issuer, audience and keys
/// are reused; no new credentials, signing keys, delegated user tokens or current-session authority are created.</remarks>
public sealed class CustomerDocumentOwnerCredential : ICustomerDocumentOwnerCredential
{
    private readonly ILegacyServiceAccessTokenProvider provider;
    private readonly HashSet<Uri> origins;
    private readonly IOptionsMonitor<JwtBearerOptions>? bearerOptions;
    private readonly IOptions<LegacyServiceAuthenticationOptions>? serviceAuthentication;
    private readonly TimeProvider timeProvider;
    /// <summary>Creates a bridge for exact HTTPS owner origins and explicitly bound existing authentication parameters.</summary>
    public CustomerDocumentOwnerCredential(ILegacyServiceAccessTokenProvider provider, IReadOnlyCollection<Uri> acceptedOwnerOrigins,
        IOptionsMonitor<JwtBearerOptions>? bearerOptions = null, IOptions<LegacyServiceAuthenticationOptions>? serviceAuthentication = null,
        TimeProvider? timeProvider = null)
    {
        this.provider = provider ?? throw new ArgumentNullException(nameof(provider));
        this.bearerOptions = bearerOptions;
        this.serviceAuthentication = serviceAuthentication;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        ArgumentNullException.ThrowIfNull(acceptedOwnerOrigins);
        if (acceptedOwnerOrigins.Count == 0 || acceptedOwnerOrigins.Any(origin =>
            !origin.IsAbsoluteUri || origin.Scheme != Uri.UriSchemeHttps || origin.UserInfo.Length != 0 ||
            origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0))
            throw new ArgumentException("Accepted HTTPS owner origins are required.", nameof(acceptedOwnerOrigins));
        origins = acceptedOwnerOrigins.ToHashSet();
    }
    /// <summary>Returns only a cryptographically validated exact own-service credential for an approved origin.</summary>
    public async Task<string?> GetAccessTokenAsync(Uri ownerOrigin, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!origins.Contains(ownerOrigin) || bearerOptions is null || serviceAuthentication?.Value.ClientId != "legacy-file") return null;
        try
        {
            var value = await provider.GetAccessTokenAsync(token);
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (ValidateOwnToken(value)) return value;
            provider.Invalidate(value);
            return null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException or
            ArgumentException or InvalidOperationException or SecurityTokenException or CryptographicException)
        {
            return null;
        }
    }
    private bool ValidateOwnToken(string value)
    {
        try
        {
            if (value.Length > 16384 || value != value.Trim() || value.Any(char.IsControl)) return false;
            var parts = value.Split('.');
            if (parts.Length != 3) return false;
            using var header = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(parts[0]), new JsonDocumentOptions { MaxDepth = 16 });
            using var payload = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(parts[1]), new JsonDocumentOptions { MaxDepth = 16 });
            if (header.RootElement.ValueKind != JsonValueKind.Object || payload.RootElement.ValueKind != JsonValueKind.Object ||
                HasDuplicates(header.RootElement) || HasDuplicates(payload.RootElement)) return false;
            var h = header.RootElement;
            if (!SingleString(h, "alg", "RS256") || !h.TryGetProperty("kid", out var kid) || kid.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(kid.GetString()) || kid.GetString()!.Length > 256 || kid.GetString()!.Any(char.IsControl)) return false;
            if (h.TryGetProperty("typ", out var type) && (type.ValueKind != JsonValueKind.String || type.GetString() != "JWT")) return false;
            var p = payload.RootElement;
            if (!SingleString(p, "sub", "service:legacy-file") || !SingleString(p, "identity_kind", "service") || !SingleString(p, "name", "legacy-file") ||
                !p.TryGetProperty("iss", out var issuer) || issuer.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(issuer.GetString()) ||
                !p.TryGetProperty("aud", out var audience) || audience.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(audience.GetString()) ||
                p.EnumerateObject().Any(property => DelegatedClaim(property.Name)) ||
                !p.TryGetProperty("iat", out var issuedValue) || !issuedValue.TryGetInt64(out var issued) ||
                !p.TryGetProperty("exp", out var expiresValue) || !expiresValue.TryGetInt64(out var expires)) return false;
            var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
            if (issued < 0 || issued > now || expires <= now || expires <= issued || expires - issued > 1800) return false;
            if (p.TryGetProperty("nbf", out var notBefore) && (!notBefore.TryGetInt64(out var nbf) || nbf > now || nbf >= expires)) return false;
            var parameters = bearerOptions!.Get(JwtBearerDefaults.AuthenticationScheme).TokenValidationParameters.Clone();
            parameters.ValidateIssuer = true;
            parameters.ValidateAudience = true;
            parameters.ValidateIssuerSigningKey = true;
            parameters.RequireSignedTokens = true;
            parameters.RequireExpirationTime = true;
            parameters.TryAllIssuerSigningKeys = false;
            parameters.ValidateLifetime = false; // Exact injected UTC clock checks above; no wall-clock skew.
            if (parameters.ValidAlgorithms is { } allowedAlgorithms && !allowedAlgorithms.Contains(SecurityAlgorithms.RsaSha256, StringComparer.Ordinal)) return false;
            parameters.ValidAlgorithms = [SecurityAlgorithms.RsaSha256];
            _ = new JwtSecurityTokenHandler { MapInboundClaims = false }.ValidateToken(value, parameters, out _);
            return true;
        }
        catch (Exception error) when (error is SecurityTokenException or JsonException or FormatException or ArgumentException or
            CryptographicException or InvalidOperationException)
        {
            return false;
        }
    }
    private static bool SingleString(JsonElement value, string name, string expected) => value.TryGetProperty(name, out var item) &&
        item.ValueKind == JsonValueKind.String && item.GetString() == expected;
    private static bool DelegatedClaim(string name)
    {
        var normalized = name.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
        return normalized is "sid" or "sessionid" or "familyid" or "employee" or "employeeid" or "customer" or "customerid" or
            "securitystamp" or "role" or "roles" or "clientid" or "userid" or "nameidentifier" || normalized.StartsWith("executor", StringComparison.Ordinal) ||
            normalized.StartsWith("delegated", StringComparison.Ordinal) || normalized.StartsWith("acting", StringComparison.Ordinal) ||
            normalized is "actor" or "actorid" or "onbehalfof" || normalized.EndsWith("/role", StringComparison.Ordinal) ||
            normalized.EndsWith("/sid", StringComparison.Ordinal) || normalized.EndsWith("/employeeid", StringComparison.Ordinal) ||
            normalized.EndsWith("/customerid", StringComparison.Ordinal) || normalized.EndsWith("/userid", StringComparison.Ordinal) ||
            normalized.EndsWith("/nameidentifier", StringComparison.Ordinal);
    }
    private static bool HasDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
                if (!names.Add(property.Name) || HasDuplicates(property.Value)) return true;
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) if (HasDuplicates(item)) return true;
        return false;
    }
}
