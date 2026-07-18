using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Models;

namespace Legacy.Maliev.FileService.Application.Services;

/// <summary>Validates and normalizes untrusted instant-quotation upload metadata.</summary>
public static class InstantQuoteFilePolicy
{
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> MediaTypes =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [".stl"] = Set("model/stl", "application/sla", "application/vnd.ms-pki.stl"),
            [".obj"] = Set("model/obj", "text/plain", "application/x-tgif"),
            [".3mf"] = Set("application/vnd.ms-package.3dmanufacturing-3dmodel+xml"),
            [".step"] = Set("model/step", "application/step"),
            [".stp"] = Set("model/step", "application/step"),
            [".iges"] = Set("model/iges", "application/iges"),
            [".igs"] = Set("model/iges", "application/iges"),
            [".glb"] = Set("model/gltf-binary"),
            [".gltf"] = Set("model/gltf+json"),
        };

    /// <summary>Validates request capability, idempotency, and expected-digest headers.</summary>
    public static InstantQuoteUploadHeaders NormalizeHeaders(
        string token,
        string idempotencyKey,
        string expectedSha256)
    {
        ValidateVisibleAscii(token, 32, 512, "Session token");
        ValidateVisibleAscii(idempotencyKey, 16, 128, "Idempotency key");

        if (expectedSha256.Length != 64 || !expectedSha256.All(Uri.IsHexDigit))
        {
            throw new InstantQuoteValidationException("Expected SHA-256 must contain exactly 64 hexadecimal characters.");
        }

        return new InstantQuoteUploadHeaders(token, idempotencyKey, expectedSha256.ToLowerInvariant());
    }

    /// <summary>Validates customer filename metadata and its declared media type.</summary>
    public static InstantQuoteNormalizedFile NormalizeFileMetadata(string fileName, string contentType)
    {
        if (string.IsNullOrEmpty(fileName) || fileName.Length > 255 || fileName is "." or ".." ||
            fileName.IndexOfAny(['/', '\\']) >= 0 ||
            fileName.EnumerateRunes().Any(rune =>
                Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format))
        {
            throw new InstantQuoteValidationException("Filename metadata is invalid.");
        }

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (!MediaTypes.TryGetValue(extension, out var allowedMediaTypes))
        {
            throw new InstantQuoteUnsupportedMediaTypeException("File extension is not supported.");
        }

        if (!MediaTypeHeaderValue.TryParse(contentType, out var parsedContentType) ||
            string.IsNullOrEmpty(parsedContentType.MediaType))
        {
            throw new InstantQuoteUnsupportedMediaTypeException("Declared media type is invalid.");
        }

        var mediaType = parsedContentType.MediaType.ToLowerInvariant();
        if (!mediaType.Equals("application/octet-stream", StringComparison.Ordinal) &&
            !allowedMediaTypes.Contains(mediaType))
        {
            throw new InstantQuoteUnsupportedMediaTypeException("Declared media type does not match the file extension.");
        }

        return new InstantQuoteNormalizedFile(
            extension,
            new InstantQuoteUploadMetadata(fileName, mediaType));
    }

    private static IReadOnlySet<string> Set(params string[] values) =>
        new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);

    private static void ValidateVisibleAscii(string value, int minimumLength, int maximumLength, string name)
    {
        if (value.Length < minimumLength || value.Length > maximumLength ||
            value.Any(character => character is < '!' or > '~'))
        {
            throw new InstantQuoteValidationException($"{name} is invalid.");
        }
    }
}

/// <summary>Normalized request headers accepted by the instant-quotation upload contract.</summary>
public sealed record InstantQuoteUploadHeaders(string Token, string IdempotencyKey, string ExpectedSha256);

/// <summary>Normalized filename metadata and lower-case supported extension.</summary>
public sealed record InstantQuoteNormalizedFile(string Extension, InstantQuoteUploadMetadata Metadata);
