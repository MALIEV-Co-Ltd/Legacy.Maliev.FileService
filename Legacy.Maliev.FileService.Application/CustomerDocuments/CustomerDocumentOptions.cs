namespace Legacy.Maliev.FileService.Application.CustomerDocuments;
/// <summary>Requires explicit separate protected document activation.</summary>
public sealed class CustomerDocumentOptions
{
    /// <summary>Gets whether the protected workflow is activated; defaults false.</summary>
    public bool Enabled { get; init; }
    /// <summary>Gets the existing private storage bucket.</summary>
    public string PrivateBucket { get; init; } = "";
    /// <summary>Gets the feature-specific maximum payload size.</summary>
    public const long MaximumBytes = 20 * 1024 * 1024;
    /// <summary>Gets the reserved prefix blocked by every legacy generic operation.</summary>
    public const string ReservedPrefix = "customer-documents/";
}
