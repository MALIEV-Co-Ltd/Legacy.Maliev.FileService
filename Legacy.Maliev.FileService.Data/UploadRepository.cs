using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Domain;
using Microsoft.EntityFrameworkCore;

namespace Legacy.Maliev.FileService.Data;

/// <summary>EF Core persistence for clean-upload metadata.</summary>
public sealed class UploadRepository(FileDbContext dbContext, TimeProvider timeProvider) : IUploadRepository
{
    /// <inheritdoc />
    public async Task AddRangeAsync(IReadOnlyCollection<Upload> uploads, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var upload in uploads)
        {
            upload.CreatedDate ??= now;
            upload.ModifiedDate ??= now;
        }

        dbContext.Uploads.AddRange(uploads);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string bucket, string objectName, CancellationToken cancellationToken) =>
        dbContext.Uploads.AsNoTracking().AnyAsync(
            upload => upload.Bucket == bucket && upload.Name == objectName,
            cancellationToken);

    /// <inheritdoc />
    public async Task DeleteAsync(string bucket, string objectName, CancellationToken cancellationToken)
    {
        await dbContext.Uploads
            .Where(upload => upload.Bucket == bucket && upload.Name == objectName)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task MoveAsync(
        string sourceBucket,
        string sourceObjectName,
        string destinationBucket,
        string destinationObjectName,
        CancellationToken cancellationToken)
    {
        var modified = timeProvider.GetUtcNow().UtcDateTime;
        await dbContext.Uploads
            .Where(upload => upload.Bucket == sourceBucket && upload.Name == sourceObjectName)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(upload => upload.Bucket, destinationBucket)
                    .SetProperty(upload => upload.Name, destinationObjectName)
                    .SetProperty(upload => upload.ModifiedDate, modified),
                cancellationToken);
    }
}
