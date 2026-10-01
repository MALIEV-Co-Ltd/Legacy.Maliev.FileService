using System.Collections;
using System.Security.Cryptography;
using Legacy.Maliev.FileService.Application.Models;

namespace Legacy.Maliev.FileService.Application.Services;

/// <summary>Owns one bounded immutable upload batch across fingerprinting, upload and scanning.</summary>
public sealed class UploadSnapshotCapture
{
    private int occupied;

    /// <summary>Captures all declared bytes before provider effects, or reuses this owner's live batch.</summary>
    public async Task<UploadSnapshotBatch> CaptureAsync(IReadOnlyList<IUploadFile> files, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (files is UploadSnapshotBatch owned && owned.BelongsTo(this)) return owned;
        if (Interlocked.CompareExchange(ref occupied, 1, 0) != 0)
            throw new UploadIdempotencyUnavailableException("Upload capacity is temporarily unavailable.");
        var captured = new List<UploadSnapshotBatch.CapturedFile>();
        try
        {
            if (files.Count == 0) throw new FileUploadValidationException("Files are required");
            var descriptors = new List<(IUploadFile File, string Name, string Type, long Length)>();
            long total = 0;
            foreach (var file in files)
            {
                var name = file.FileName; var type = file.ContentType; var length = file.Length;
                if (string.IsNullOrWhiteSpace(name) || length <= 0)
                    throw new FileUploadValidationException("Every file must have a name and content");
                if (length > FileApplicationService.MaximumUploadBytes - total)
                    throw new FileUploadValidationException("Total upload size cannot exceed 100 MB");
                total += length;
                descriptors.Add((file, name, type, length));
            }
            foreach (var descriptor in descriptors)
            {
                token.ThrowIfCancellationRequested();
                var item = new UploadSnapshotBatch.CapturedFile(descriptor.Name, descriptor.Type, new byte[(int)descriptor.Length]);
                captured.Add(item);
                await using var source = descriptor.File.OpenReadStream();
                try { await source.ReadExactlyAsync(item.Bytes, token); }
                catch (EndOfStreamException) { throw new FileUploadValidationException("File content does not match its declared length"); }
                var extra = new byte[1];
                if (await source.ReadAsync(extra, token) != 0)
                    throw new FileUploadValidationException("File content does not match its declared length");
            }
            return new UploadSnapshotBatch(this, captured);
        }
        catch
        {
            foreach (var item in captured) item.Clear();
            Release();
            throw;
        }
    }

    internal void Release() => Volatile.Write(ref occupied, 0);
}

/// <summary>An internally issued, disposable batch with immutable metadata and independent readonly streams.</summary>
public sealed class UploadSnapshotBatch : IReadOnlyList<IUploadFile>, IDisposable
{
    private readonly UploadSnapshotCapture owner;
    private readonly IReadOnlyList<CapturedFile> files;
    private int disposed;
    internal UploadSnapshotBatch(UploadSnapshotCapture owner, IReadOnlyList<CapturedFile> files)
    { this.owner = owner; this.files = files; }
    internal bool BelongsTo(UploadSnapshotCapture candidate) => ReferenceEquals(owner, candidate) && Volatile.Read(ref disposed) == 0;
    int IReadOnlyCollection<IUploadFile>.Count => files.Count;
    IUploadFile IReadOnlyList<IUploadFile>.this[int index] => files[index];
    IEnumerator<IUploadFile> IEnumerable<IUploadFile>.GetEnumerator() => files.Cast<IUploadFile>().GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => files.GetEnumerator();

    /// <summary>Clears all owned bytes and releases admission once; metadata remains available.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        foreach (var file in files) file.Clear();
        owner.Release();
    }

    internal sealed class CapturedFile(string name, string type, byte[] bytes) : IUploadFile
    {
        private readonly long length = bytes.LongLength;
        private byte[]? content = bytes;
        internal byte[] Bytes => content ?? throw new ObjectDisposedException(nameof(CapturedFile));
        public string FileName => name;
        public string ContentType => type;
        public long Length => length;
        public Stream OpenReadStream()
        {
            var owned = Bytes;
            return new MemoryStream(owned, 0, owned.Length, writable: false, publiclyVisible: false);
        }
        internal void Clear()
        {
            var owned = Interlocked.Exchange(ref content, null);
            if (owned is not null) CryptographicOperations.ZeroMemory(owned);
        }
    }
}
