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
            ".gltf" => IsGltf(prefix, sizeBytes),
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

    private static bool IsGltf(ReadOnlySpan<byte> prefix, long sizeBytes)
    {
        try
        {
            var isFinalBlock = sizeBytes <= prefix.Length;
            var reader = new Utf8JsonReader(prefix, isFinalBlock, default);
            var rootObject = false;
            var rootComplete = false;

            while (reader.Read())
            {
                if (!rootObject)
                {
                    if (reader.TokenType != JsonTokenType.StartObject || reader.CurrentDepth != 0)
                    {
                        return false;
                    }

                    rootObject = true;
                    continue;
                }

                if (rootComplete)
                {
                    return false;
                }

                if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 0)
                {
                    rootComplete = true;
                }
            }

            return rootObject && (!isFinalBlock || rootComplete);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ContainsAscii(ReadOnlySpan<byte> value, string expected) =>
        Encoding.ASCII.GetString(value).Contains(expected, StringComparison.OrdinalIgnoreCase);
}
