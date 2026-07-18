using System.Text.Json.Serialization;

namespace Legacy.Maliev.FileService.Application.Models;

/// <summary>Constants shared by the instant-quotation upload contract.</summary>
public static class InstantQuoteFileContract
{
    /// <summary>Maximum number of uploaded file bytes accepted by the workflow.</summary>
    public const long MaximumUploadBytes = 200L * 1024 * 1024;

    /// <summary>Exact case-insensitive extension allowlist for instant quotations.</summary>
    public static IReadOnlyList<string> SupportedExtensions { get; } = Array.AsReadOnly([
        ".stl",
        ".obj",
        ".3mf",
        ".step",
        ".stp",
        ".iges",
        ".igs",
        ".glb",
        ".gltf",
    ]);
}

/// <summary>Identifies the authenticated or anonymous principal that creates an upload session.</summary>
/// <param name="PrincipalId">Stable authenticated principal identifier, when available.</param>
/// <param name="IsAuthenticated">Whether the request principal was authenticated.</param>
public sealed record InstantQuoteOwner(
    [property: JsonPropertyName("principalId")] string? PrincipalId,
    [property: JsonPropertyName("isAuthenticated")] bool IsAuthenticated);

/// <summary>Returns a new opaque upload-session capability and its expiry.</summary>
/// <param name="SessionId">Opaque session identifier.</param>
/// <param name="SessionToken">One-time-disclosed capability token used to prove session ownership.</param>
/// <param name="ExpiresAt">UTC instant after which the session cannot be used.</param>
public sealed record CreateInstantQuoteSessionResponse(
    [property: JsonPropertyName("sessionId")] Guid SessionId,
    [property: JsonPropertyName("sessionToken")] string SessionToken,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt);

/// <summary>Safe customer-supplied metadata for a streamed upload.</summary>
/// <param name="FileName">Customer filename retained as metadata only.</param>
/// <param name="ContentType">Declared media type.</param>
public sealed record InstantQuoteUploadMetadata(
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("contentType")] string ContentType);

/// <summary>Describes a file accepted by the instant-quotation workflow.</summary>
/// <param name="FileId">Opaque file identifier.</param>
/// <param name="FileName">Customer filename metadata.</param>
/// <param name="ContentType">Validated media type.</param>
/// <param name="SizeBytes">Verified byte count.</param>
/// <param name="Sha256">Verified hexadecimal SHA-256 digest.</param>
/// <param name="Status">Current workflow status.</param>
public sealed record InstantQuoteFileResponse(
    [property: JsonPropertyName("fileId")] Guid FileId,
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("contentType")] string ContentType,
    [property: JsonPropertyName("sizeBytes")] long SizeBytes,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("status")] string Status);

/// <summary>Selects clean uploaded files for a quotation request.</summary>
/// <param name="QuotationRequestId">Identifier owned by the Web quotation workflow.</param>
/// <param name="FileIds">Files selected from this upload session.</param>
public sealed record FinalizeInstantQuoteFilesRequest(
    [property: JsonPropertyName("quotationRequestId")] Guid QuotationRequestId,
    [property: JsonPropertyName("fileIds")] IReadOnlyList<Guid> FileIds);

/// <summary>Returns the files linked to the quotation request.</summary>
/// <param name="QuotationRequestId">Identifier owned by the Web quotation workflow.</param>
/// <param name="Files">Authoritative finalized file results.</param>
public sealed record FinalizeInstantQuoteFilesResponse(
    [property: JsonPropertyName("quotationRequestId")] Guid QuotationRequestId,
    [property: JsonPropertyName("files")] IReadOnlyList<InstantQuoteFileResponse> Files);
