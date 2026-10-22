namespace Legacy.Maliev.FileService.Application.CustomerDocuments;
/// <summary>Controls separately authorized runtime activation of the registry.</summary>
public sealed class CustomerDocumentRegistryOptions
{
    /// <summary>Enables registry readback; defaults disabled.</summary>
    public bool Enabled { get; init; }
}
