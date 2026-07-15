namespace Legacy.Maliev.FileService.Api.Authorization;

/// <summary>Granular permissions for protected legacy file endpoints.</summary>
public static class FilePermissions
{
    /// <summary>Upload and promote permission.</summary>
    public const string Create = "legacy-file.uploads.create";
    /// <summary>Signed read URL permission.</summary>
    public const string Read = "legacy-file.uploads.read";
    /// <summary>Object move permission.</summary>
    public const string Update = "legacy-file.uploads.update";
    /// <summary>Object delete permission.</summary>
    public const string Delete = "legacy-file.uploads.delete";
}
