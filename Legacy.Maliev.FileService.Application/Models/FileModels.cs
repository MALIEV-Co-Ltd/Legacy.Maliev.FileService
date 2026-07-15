namespace Legacy.Maliev.FileService.Application.Models;

/// <summary>Preserves the legacy upload response envelope.</summary>
public sealed record UploadResultResponse(IReadOnlyList<UploadObjectResponse> Object);

/// <summary>Preserves the legacy uploaded-object response.</summary>
public sealed record UploadObjectResponse(string Bucket, string ObjectName, Uri Uri);

/// <summary>Represents an incoming file without coupling application code to ASP.NET.</summary>
public interface IUploadFile
{
    /// <summary>Gets the client-supplied file name.</summary>
    string FileName { get; }
    /// <summary>Gets the declared content type.</summary>
    string ContentType { get; }
    /// <summary>Gets the file length.</summary>
    long Length { get; }
    /// <summary>Opens a fresh readable stream.</summary>
    Stream OpenReadStream();
}

/// <summary>Malware-scanner verdict.</summary>
public enum FileSafetyVerdict
{
    /// <summary>The complete object was scanned and is clean.</summary>
    Clean,
    /// <summary>The scanner identified malicious content.</summary>
    Infected,
    /// <summary>The scanner could not establish that the object is clean.</summary>
    Unavailable,
}

/// <summary>Result returned by a malware scanner.</summary>
public sealed record FileSafetyResult(FileSafetyVerdict Verdict, string? ThreatName = null);
