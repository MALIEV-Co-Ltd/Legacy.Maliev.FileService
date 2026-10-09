using Legacy.Maliev.FileService.Application.CustomerDocuments;
using Legacy.Maliev.FileService.Application.Models;
namespace Legacy.Maliev.FileService.Tests.CustomerDocuments;
public sealed class ProtectedDocumentUploadTests
{
    [Theory]
    [InlineData("a.pdf", "application/pdf", "%PDF-1.7\nbody")]
    [InlineData("a.png", "image/png", "%PDF-1.7\n%%EOF")]
    [InlineData("../a.pdf", "application/pdf", "%PDF-1.7\n%%EOF")]
    public async Task InvalidContentOrNameIsRejected(string name, string mime, string bytes)
    {
        await Assert.ThrowsAsync<DocumentContentException>(() => DocumentContentValidator.ReadAsync(new File(name, mime, System.Text.Encoding.ASCII.GetBytes(bytes)), default));
    }
    [Fact]
    public async Task DeclaredOversizeIsRefusedBeforeReadingBytes()
    {
        var file = new DeclaredFile(CustomerDocumentOptions.MaximumBytes + 1, []);
        var failure = await Assert.ThrowsAsync<DocumentContentException>(() => DocumentContentValidator.ReadAsync(file, default));
        Assert.Equal(413, failure.StatusCode); Assert.False(file.Opened);
    }
    [Fact]
    public async Task ActualStreamOversizeCannotHideBehindSmallDeclaredLength()
    {
        var file = new DeclaredFile(1, new byte[CustomerDocumentOptions.MaximumBytes + 1]);
        var failure = await Assert.ThrowsAsync<DocumentContentException>(() => DocumentContentValidator.ReadAsync(file, default));
        Assert.Equal(413, failure.StatusCode);
    }
    [Fact]
    public async Task DeclaredLengthMismatchRefusesCompletePayload()
    {
        var failure = await Assert.ThrowsAsync<DocumentContentException>(() => DocumentContentValidator.ReadAsync(new DeclaredFile(1, "%PDF-1.7\n1 0 obj <<>> endobj\n%%EOF\n"u8.ToArray()), default));
        Assert.Equal(415, failure.StatusCode);
    }
    [Theory]
    [InlineData("png", "image/png", "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAANSURBVBhXY1BwSPgPAAMEAcCv7daaAAAAAElFTkSuQmCC")]
    [InlineData("jpg", "image/jpeg", "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAMCAgMCAgMDAwMEAwMEBQgFBQQEBQoHBwYIDAoMDAsKCwsNDhIQDQ4RDgsLEBYQERMUFRUVDA8XGBYUGBIUFRT/2wBDAQMEBAUEBQkFBQkUDQsNFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBT/wAARCAABAAEDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwD5Eooor7g+MP/Z")]
    public async Task ActualEncodedImagesAreAcceptedWithExactDigest(string extension, string type, string encoded)
    {
        // One-pixel RGB fixtures encoded with the Windows image encoder, not parser-shaped dummy content.
        var bytes = Convert.FromBase64String(encoded);
        var result = await DocumentContentValidator.ReadAsync(new File($"synthetic.{extension}", type, bytes), default);
        Assert.Equal(type, result.ContentType); Assert.Equal(bytes, result.Bytes.ToArray());
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), result.Sha256);
    }
    [Fact]
    public async Task ValidPdfIsBoundedHashedAndDetachedFromCaller()
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj <<>> endobj\n%%EOF\n");
        var result = await DocumentContentValidator.ReadAsync(new File("agreement.pdf", "application/pdf", bytes), default);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), result.Sha256);
        Assert.Equal("application/pdf", result.ContentType);
        bytes[0] = 0;
        Assert.Equal((byte)'%', result.Bytes.Span[0]);
    }
    private sealed record File(string FileName, string ContentType, byte[] Bytes) : IUploadFile
    {
        public long Length => Bytes.LongLength;
        public Stream OpenReadStream() => new MemoryStream(Bytes, false);
    }
    private sealed class DeclaredFile(long declaredLength, byte[] bytes) : IUploadFile
    {
        public string FileName => "synthetic.pdf";
        public string ContentType => "application/pdf";
        public long Length => declaredLength;
        public bool Opened { get; private set; }
        public Stream OpenReadStream() { Opened = true; return new MemoryStream(bytes, false); }
    }
}
