using Legacy.Maliev.FileService.Application.Models;
using Microsoft.Extensions.Options;

namespace Legacy.Maliev.FileService.Application.Services;

/// <summary>Enforces the independent runtime and write authorities for the legacy file workflow.</summary>
public sealed class LegacyFileRuntimeGate(IOptions<FileStorageOptions> options)
{
    /// <summary>Fails closed unless this runtime explicitly enables legacy storage writes.</summary>
    public void EnsureWritesEnabled()
    {
        if (!options.Value.Enabled || !options.Value.WritesEnabled)
        {
            throw new MalwareScannerUnavailableException("Legacy file writes are disabled.");
        }
    }
}
