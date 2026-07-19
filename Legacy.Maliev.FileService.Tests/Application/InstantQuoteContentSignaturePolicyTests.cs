using System.Text;
using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Services;

namespace Legacy.Maliev.FileService.Tests.Application;

public sealed class InstantQuoteContentSignaturePolicyTests
{
    public static TheoryData<string, byte[], long> ValidCadSignatures => new()
    {
        { ".stl", Encoding.ASCII.GetBytes("solid example\nendsolid example"), 30 },
        { ".obj", Encoding.ASCII.GetBytes("# example\nv 0 0 0\nf 1 1 1"), 27 },
        { ".3mf", [0x50, 0x4b, 0x03, 0x04], 4 },
        { ".step", Encoding.ASCII.GetBytes("ISO-10303-21;"), 13 },
        { ".stp", Encoding.ASCII.GetBytes("ISO-10303-21;"), 13 },
        { ".iges", IgesPrefix(), 73 },
        { ".igs", IgesPrefix(), 73 },
        { ".glb", Encoding.ASCII.GetBytes("glTF"), 4 },
        { ".gltf", Encoding.UTF8.GetBytes("{\"asset\":{\"version\":\"2.0\"}}"), 27 },
    };

    [Theory]
    [MemberData(nameof(ValidCadSignatures))]
    public void Validate_SupportedCadSignature_AcceptsEveryDocumentedExtension(
        string extension,
        byte[] prefix,
        long sizeBytes)
    {
        InstantQuoteContentSignaturePolicy.Validate(extension, prefix, sizeBytes);
    }

    [Fact]
    public void Validate_GltfWhoseValidDocumentContinuesBeyondPrefix_AcceptsBoundedStructuralPrefix()
    {
        var prefix = Encoding.UTF8.GetBytes(
            "{\"asset\":{\"version\":\"2.0\"},\"buffers\":[{\"uri\":\"" + new string('a', 5000));

        InstantQuoteContentSignaturePolicy.Validate(".gltf", prefix.AsSpan(0, 4096), prefix.Length + 100);
    }

    [Fact]
    public void Validate_GltfWhoseAssetPropertyIsAfterCapturedPrefix_AcceptsOrderIndependentStructuralPrefix()
    {
        var document = Encoding.UTF8.GetBytes(
            "{\"extras\":{\"padding\":\"" + new string('a', 5000) + "\"},\"asset\":{\"version\":\"2.0\"}}");

        InstantQuoteContentSignaturePolicy.Validate(".gltf", document.AsSpan(0, 4096), document.Length);
    }

    [Fact]
    public void Validate_GltfObjectWithoutAssetMarker_AcceptsBoundedPlausibilityCheck()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"scene\":0}");

        InstantQuoteContentSignaturePolicy.Validate(".gltf", bytes, bytes.Length);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"asset\":{\"version\":\"2.0\"}")]
    [InlineData("{\"asset\":{\"version\":\"2.0\"},,}")]
    public void Validate_GltfMalformedOrNonObjectDocument_RejectsContent(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);

        Assert.Throws<InstantQuoteUnsafeContentException>(() =>
            InstantQuoteContentSignaturePolicy.Validate(".gltf", bytes, bytes.Length));
    }

    private static byte[] IgesPrefix()
    {
        var prefix = Enumerable.Repeat((byte)' ', 73).ToArray();
        prefix[72] = (byte)'S';
        return prefix;
    }
}
