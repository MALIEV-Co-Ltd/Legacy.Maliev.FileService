namespace Legacy.Maliev.FileService.Application.Interfaces;

/// <summary>Durable private coordinates before initial upload; never certifies clean bytes or authorizes automatic retry.</summary>
public interface IQuarantineUploadIntent
{
    /// <summary>Verifies physical recovery schema and creates a new intent; an existing identity fails closed.</summary>
    Task PrepareAsync(Guid operationId, Guid parentOperationId, string bucket, string objectName,
        string contentType, long declaredSize, CancellationToken cancellationToken);
    /// <summary>Attaches only a positive generation acknowledged by the provider to the pending intent.</summary>
    Task AcknowledgeAsync(Guid operationId, long generation, CancellationToken cancellationToken);
    /// <summary>Preserves coordinates and any generation when provider or checkpoint outcome is uncertain.</summary>
    Task UnknownAsync(Guid operationId, CancellationToken cancellationToken);
}
