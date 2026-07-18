using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.FileService.Application.Interfaces;

namespace Legacy.Maliev.FileService.Application.Services;

/// <summary>Performs bounded, plausibility-only CAD signature checks before scanning.</summary>
public static class InstantQuoteContentSignaturePolicy
{
    /// <summary>Rejects content whose bounded prefix cannot plausibly match its validated extension.</summary>
    public static void Validate(string extension, ReadOnlySpan<byte> prefix, long sizeBytes)
    {
        var valid = extension switch
        {
            ".stl" => IsStl(prefix, sizeBytes),
            ".obj" => IsObj(prefix),
            ".3mf" => prefix.StartsWith("PK\u0003\u0004"u8),
            ".step" or ".stp" => ContainsAscii(prefix, "ISO-10303-21"),
            ".iges" or ".igs" => prefix.Length >= 73 && prefix[72] == (byte)'S',
            ".glb" => prefix.StartsWith("glTF"u8),
            ".gltf" => IsJson(prefix),
            _ => false,
        };

        if (!valid)
        {
            throw new InstantQuoteUnsafeContentException("Uploaded content does not match its declared CAD format.");
        }
    }

    private static bool IsStl(ReadOnlySpan<byte> prefix, long sizeBytes)
    {
        var first = 0;
        while (first < prefix.Length && prefix[first] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            first++;
        }
        if (prefix[first..].StartsWith("solid"u8))
        {
            return true;
        }

        if (prefix.Length < 84 || sizeBytes < 84)
        {
            return false;
        }

        var triangleCount = BinaryPrimitives.ReadUInt32LittleEndian(prefix.Slice(80, 4));
        return 84L + (50L * triangleCount) == sizeBytes;
    }

    private static bool IsObj(ReadOnlySpan<byte> prefix) =>
        ContainsAscii(prefix, "v ") || ContainsAscii(prefix, "o ") || ContainsAscii(prefix, "f ");

    private static bool IsJson(ReadOnlySpan<byte> prefix)
    {
        try
        {
            using var document = JsonDocument.Parse(prefix.ToArray());
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ContainsAscii(ReadOnlySpan<byte> value, string expected) =>
        Encoding.ASCII.GetString(value).Contains(expected, StringComparison.OrdinalIgnoreCase);
}
