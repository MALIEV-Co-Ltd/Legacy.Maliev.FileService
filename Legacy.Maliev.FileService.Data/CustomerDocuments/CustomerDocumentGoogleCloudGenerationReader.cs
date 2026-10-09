using System.Net;
using Google;
using Google.Cloud.Storage.V1;
using Legacy.Maliev.FileService.Application.CustomerDocuments;
using Microsoft.Extensions.Options;
namespace Legacy.Maliev.FileService.Data.CustomerDocuments;
/// <summary>Reads exact private GCS generations independently of unrelated feature flags.</summary>
public sealed class CustomerDocumentGoogleCloudGenerationReader(StorageClient client, IOptions<CustomerDocumentOptions> options) : IProtectedDocumentGenerationReader
{
    /// <inheritdoc />
    public async Task<ReadOnlyMemory<byte>> ReadAsync(string bucket, string objectName, long generation, long maximumBytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!options.Value.Enabled || bucket != options.Value.PrivateBucket || !objectName.StartsWith(CustomerDocumentOptions.ReservedPrefix, StringComparison.Ordinal) || generation <= 0 || maximumBytes is <= 0 or > CustomerDocumentOptions.MaximumBytes) throw new DocumentAuthorityUnavailableException();
        using var output = new BoundedDestination(maximumBytes);
        try
        {
            var metadata = await client.DownloadObjectAsync(bucket, objectName, output, new DownloadObjectOptions { Generation = generation, IfGenerationMatch = generation }, token);
            if (metadata.Generation != generation) throw new DocumentAuthorityUnavailableException();
            return output.Bytes();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (GoogleApiException exception) when (exception.HttpStatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed) { throw new DocumentAuthorityDeniedException(); }
        catch (DocumentAuthorityDeniedException) { throw; }
        catch (DocumentAuthorityUnavailableException) { throw; }
        catch (Exception) { throw new DocumentAuthorityUnavailableException(); }
    }
    private sealed class BoundedDestination(long maximum) : Stream
    {
        private readonly MemoryStream output = new();
        public ReadOnlyMemory<byte> Bytes() => output.ToArray();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => output.Length;
        public override long Position { get => output.Length; set => throw new NotSupportedException(); }
        public override void Flush() => output.Flush();
        public override Task FlushAsync(CancellationToken token) => output.FlushAsync(token);
        public override void Write(byte[] buffer, int offset, int count) { EnsureBound(count); output.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { EnsureBound(buffer.Length); output.Write(buffer); }
        public override void WriteByte(byte value) { EnsureBound(1); output.WriteByte(value); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) { token.ThrowIfCancellationRequested(); EnsureBound(count); return output.WriteAsync(buffer, offset, count, token); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) { token.ThrowIfCancellationRequested(); EnsureBound(buffer.Length); return output.WriteAsync(buffer, token); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        private void EnsureBound(int count) { if (count < 0 || count > maximum - output.Length) throw new DocumentAuthorityUnavailableException(); }
        protected override void Dispose(bool disposing) { if (disposing) output.Dispose(); base.Dispose(disposing); }
    }
}
