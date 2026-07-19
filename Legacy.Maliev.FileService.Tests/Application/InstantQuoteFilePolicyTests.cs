using Legacy.Maliev.FileService.Application.Interfaces;
using Legacy.Maliev.FileService.Application.Services;

namespace Legacy.Maliev.FileService.Tests.Application;

public sealed class InstantQuoteFilePolicyTests
{
    public static TheoryData<string, string, string> SupportedTypes => new()
    {
        { "part.stl", "model/stl", ".stl" },
        { "part.stl", "application/sla", ".stl" },
        { "part.stl", "application/vnd.ms-pki.stl", ".stl" },
        { "part.OBJ", "model/obj", ".obj" },
        { "part.obj", "text/plain", ".obj" },
        { "part.obj", "application/x-tgif", ".obj" },
        { "part.3mf", "application/vnd.ms-package.3dmanufacturing-3dmodel+xml", ".3mf" },
        { "part.step", "model/step", ".step" },
        { "part.step", "application/step", ".step" },
        { "part.stp", "model/step", ".stp" },
        { "part.stp", "application/step", ".stp" },
        { "part.iges", "model/iges", ".iges" },
        { "part.iges", "application/iges", ".iges" },
        { "part.igs", "model/iges", ".igs" },
        { "part.igs", "application/iges", ".igs" },
        { "part.glb", "model/gltf-binary", ".glb" },
        { "part.gltf", "model/gltf+json", ".gltf" },
    };

    public static TheoryData<string, string> SupportedExtensions => new()
    {
        { "part.stl", ".stl" },
        { "part.OBJ", ".obj" },
        { "part.3mf", ".3mf" },
        { "part.step", ".step" },
        { "part.stp", ".stp" },
        { "part.iges", ".iges" },
        { "part.igs", ".igs" },
        { "part.glb", ".glb" },
        { "part.gltf", ".gltf" },
    };

    [Theory]
    [MemberData(nameof(SupportedTypes))]
    public void NormalizeFileMetadata_SupportedExtensionAndMediaType_ReturnsNormalizedExtension(
        string fileName,
        string contentType,
        string expectedExtension)
    {
        var result = InstantQuoteFilePolicy.NormalizeFileMetadata(fileName, contentType);

        Assert.Equal(expectedExtension, result.Extension);
        Assert.Equal(contentType, result.Metadata.ContentType);
    }

    [Theory]
    [MemberData(nameof(SupportedExtensions))]
    public void NormalizeFileMetadata_OctetStreamForSupportedExtension_IsAccepted(
        string fileName,
        string expectedExtension)
    {
        var result = InstantQuoteFilePolicy.NormalizeFileMetadata(fileName, "application/octet-stream");

        Assert.Equal(expectedExtension, result.Extension);
    }

    [Theory]
    [InlineData("part.exe", "application/octet-stream")]
    [InlineData("part.stl", "image/png")]
    [InlineData("part.stl", "not a media type")]
    public void NormalizeFileMetadata_UnsupportedOrMismatchedType_Throws(string fileName, string contentType)
    {
        Assert.Throws<InstantQuoteUnsupportedMediaTypeException>(() =>
            InstantQuoteFilePolicy.NormalizeFileMetadata(fileName, contentType));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("folder/part.stl")]
    [InlineData("folder\\part.stl")]
    [InlineData("part\u0001.stl")]
    [InlineData("part\u202E.stl")]
    public void NormalizeFileMetadata_UnsafeFileName_Throws(string fileName)
    {
        Assert.Throws<InstantQuoteValidationException>(() =>
            InstantQuoteFilePolicy.NormalizeFileMetadata(fileName, "application/octet-stream"));
    }

    [Fact]
    public void NormalizeFileMetadata_OverlongFileName_Throws()
    {
        Assert.Throws<InstantQuoteValidationException>(() =>
            InstantQuoteFilePolicy.NormalizeFileMetadata($"{new string('a', 252)}.stl", "model/stl"));
    }

    [Theory]
    [InlineData("short", "idem-01234567890", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("token-value", "bad\u0001key-0123456", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("token-value", "idem-01234567890", "xyz")] // gitleaks:allow -- deterministic invalid-header fixture
    [InlineData("token-value", "idem-01234567890", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAG")] // gitleaks:allow -- deterministic invalid-header fixture
    public void NormalizeHeaders_InvalidValue_Throws(string token, string idempotencyKey, string digest)
    {
        Assert.Throws<InstantQuoteValidationException>(() =>
            InstantQuoteFilePolicy.NormalizeHeaders(token, idempotencyKey, digest));
    }

    [Fact]
    public void NormalizeHeaders_ValidValues_NormalizesDigestLowercase()
    {
        var result = InstantQuoteFilePolicy.NormalizeHeaders(
            new string('t', 32),
            new string('i', 16),
            new string('A', 64));

        Assert.Equal(new string('a', 64), result.ExpectedSha256);
    }
}
