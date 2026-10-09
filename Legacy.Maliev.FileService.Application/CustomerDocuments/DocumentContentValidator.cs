using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Legacy.Maliev.FileService.Application.Models;
namespace Legacy.Maliev.FileService.Application.CustomerDocuments;
/// <summary>Rejects malformed protected document content.</summary>
public sealed class DocumentContentException(int statusCode) : Exception
{
    /// <summary>Gets the safe HTTP classification.</summary>
    public int StatusCode { get; } = statusCode;
}
/// <summary>Holds detached bounded upload bytes.</summary>
public sealed record ValidatedDocumentContent(ReadOnlyMemory<byte> Bytes, string ContentType, string Sha256, string FileName);
/// <summary>Validates the entire bounded payload before storage or scanning.</summary>
public static class DocumentContentValidator
{
    /// <summary>Reads and validates complete bounded document bytes.</summary>
    public static async Task<ValidatedDocumentContent> ReadAsync(IUploadFile file, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(file.FileName) || file.FileName.Length > 200 || file.FileName.IndexOfAny(['/', '\\', ':', '\0', '\r', '\n']) >= 0 || file.FileName.Any(char.IsControl) || file.FileName is "." or "..") throw new DocumentContentException(400);
        if (file.Length > CustomerDocumentOptions.MaximumBytes) throw new DocumentContentException(413);
        if (file.Length <= 0) throw new DocumentContentException(415);
        using var output = new MemoryStream();
        using var input = file.OpenReadStream();
        var buffer = new byte[81920];
        int count;
        while ((count = await input.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + count > CustomerDocumentOptions.MaximumBytes) throw new DocumentContentException(413);
            await output.WriteAsync(buffer.AsMemory(0, count), token);
        }
        if (output.Length != file.Length) throw new DocumentContentException(415);
        var bytes = output.ToArray();
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        var type = Detect(bytes);
        if (type is null || type != file.ContentType.ToLowerInvariant() || !(type switch { "application/pdf" => extension == ".pdf", "image/png" => extension == ".png", "image/jpeg" => extension is ".jpg" or ".jpeg", _ => false })) throw new DocumentContentException(415);
        return new(bytes, type, Convert.ToHexStringLower(SHA256.HashData(bytes)), file.FileName);
    }
    private static string? Detect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith("%PDF-"u8))
        {
            var text = Encoding.Latin1.GetString(bytes);
            return text.Length >= 16 && text.TrimEnd().EndsWith("%%EOF", StringComparison.Ordinal) && text.Contains("obj", StringComparison.Ordinal) && text.Contains("endobj", StringComparison.Ordinal) ? "application/pdf" : null;
        }
        if (ValidPng(bytes)) return "image/png";
        if (ValidJpeg(bytes)) return "image/jpeg";
        return null;
    }
    private static bool ValidPng(ReadOnlySpan<byte> bytes)
    {
        if (!bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return false;
        var offset = 8;
        var header = false;
        var data = false;
        while (offset + 12 <= bytes.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]);
            if (length > int.MaxValue || (long)offset + length + 12 > bytes.Length) return false;
            var size = (int)length;
            var kind = bytes.Slice(offset + 4, 4);
            var payload = bytes.Slice(offset + 4, size + 4);
            uint crc = uint.MaxValue;
            foreach (var value in payload)
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 1 ? 0xedb88320u : 0u);
            }
            if (~crc != BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset + size + 8, 4))) return false;
            if (!header)
            {
                if (!kind.SequenceEqual("IHDR"u8) || size != 13 || BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset + 8, 4)) == 0 || BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset + 12, 4)) == 0) return false;
                header = true;
            }
            else if (kind.SequenceEqual("IHDR"u8)) return false;
            if (kind.SequenceEqual("IDAT"u8) && size > 0) data = true;
            offset += size + 12;
            if (kind.SequenceEqual("IEND"u8)) return size == 0 && data && offset == bytes.Length;
        }
        return false;
    }
    private static bool ValidJpeg(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12 || bytes[0] != 0xff || bytes[1] != 0xd8 || bytes[^2] != 0xff || bytes[^1] != 0xd9) return false;
        var frame = false;
        for (var offset = 2; offset < bytes.Length - 2;)
        {
            if (bytes[offset++] != 0xff || offset >= bytes.Length) return false;
            while (offset < bytes.Length && bytes[offset] == 0xff) offset++;
            if (offset >= bytes.Length) return false;
            var marker = bytes[offset++];
            if (marker == 0xda) return frame && offset + 2 < bytes.Length - 2 && BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]) >= 2 && offset + BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]) < bytes.Length - 2;
            if (marker is 0 or 0xd8 or 0xd9 || offset + 2 > bytes.Length) return false;
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]);
            if (length < 2 || offset + length > bytes.Length - 2) return false;
            if (marker is >= 0xc0 and <= 0xc3)
            {
                if (length < 8 || BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 3, 2)) == 0 || BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 5, 2)) == 0) return false;
                frame = true;
            }
            offset += length;
        }
        return false;
    }
}
