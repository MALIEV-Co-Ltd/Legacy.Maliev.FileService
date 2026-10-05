namespace Legacy.Maliev.FileService.Application.Services;

/// <summary>Refuses an unsafe or unresolved generation read without disclosing private coordinates.</summary>
public sealed class SignedReadEvidenceUnavailableException() : Exception("Signed read evidence is unavailable.");
