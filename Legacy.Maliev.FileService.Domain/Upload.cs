namespace Legacy.Maliev.FileService.Domain;

/// <summary>Metadata for a clean object promoted into legacy storage.</summary>
public sealed class Upload
{
    /// <summary>Gets or sets the legacy identifier.</summary>
    public int Id { get; set; }
    /// <summary>Gets or sets the Google Cloud Storage bucket.</summary>
    public string Bucket { get; set; } = string.Empty;
    /// <summary>Gets or sets the object content type.</summary>
    public string ContentType { get; set; } = string.Empty;
    /// <summary>Gets or sets the object name.</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>Gets or sets the object size in bytes.</summary>
    public long Size { get; set; }
    /// <summary>Gets or sets the creation timestamp.</summary>
    public DateTime? CreatedDate { get; set; }
    /// <summary>Gets or sets the modification timestamp.</summary>
    public DateTime? ModifiedDate { get; set; }
}
