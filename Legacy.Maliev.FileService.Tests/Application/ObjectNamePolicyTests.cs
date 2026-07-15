using Legacy.Maliev.FileService.Application.Models;
using Legacy.Maliev.FileService.Application.Services;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Legacy.Maliev.FileService.Tests.Application;

public sealed class ObjectNamePolicyTests
{
    private readonly ObjectNamePolicy policy = new(
        Options.Create(new FileStorageOptions { AllowedBuckets = ["maliev.com"] }),
        new FakeTimeProvider(new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero)));

    [Fact]
    public void BuildFinalObjectName_CustomPath_PreservesLegacyLowerCaseShape()
    {
        var result = policy.BuildFinalObjectName("Uploads\\Customer/", "PART.STL", Guid.Empty);

        Assert.Equal("uploads/customer/part.stl", result);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("uploads/../../escape")]
    [InlineData("uploads/./escape")]
    public void BuildFinalObjectName_Traversal_Rejects(string path)
    {
        Assert.Throws<FileUploadValidationException>(() => policy.BuildFinalObjectName(path, "part.stl", Guid.Empty));
    }

    [Fact]
    public void RequireBucket_UnknownBucket_Rejects()
    {
        Assert.Throws<FileUploadValidationException>(() => policy.RequireBucket("attacker-bucket"));
    }
}
