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
    [InlineData("2026-07-15T18:00:00Z", 7, "2026-7-16")]
    [InlineData("2026-07-31T18:00:00Z", 7, "2026-8-1")]
    [InlineData("2026-12-31T18:00:00Z", 7, "2027-1-1")]
    [InlineData("2026-07-15T02:00:00Z", -8, "2026-7-14")]
    [InlineData("2026-08-01T02:00:00Z", -8, "2026-7-31")]
    [InlineData("2027-01-01T02:00:00Z", -8, "2026-12-31")]
    [InlineData("2026-07-15T18:00:00Z", 0, "2026-7-15")]
    [InlineData("2026-08-01T02:00:00Z", 0, "2026-8-1")]
    [InlineData("2027-01-01T02:00:00Z", 0, "2027-1-1")]
    public void BuildFinalObjectName_DefaultPrefix_UsesMachineLocalDateWithoutChangingCustomPrefix(
        string utcInstant, int offsetHours, string expectedDate)
    {
        var instant = DateTimeOffset.Parse(utcInstant, System.Globalization.CultureInfo.InvariantCulture);
        var clock = new FixedLocalTimeProvider(instant, TimeSpan.FromHours(offsetHours));
        var localPolicy = new ObjectNamePolicy(
            Options.Create(new FileStorageOptions { AllowedBuckets = ["maliev.com"] }), clock);
        var uploadId = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");

        var expectedPath = $"uploads/{expectedDate}/{uploadId}/part.stl";
        var actualPath = localPolicy.BuildFinalObjectName(null, "PART.STL", uploadId);
        Assert.True(expectedPath == actualPath,
            $"Legacy upload date path mismatch; expected path={expectedPath}; actual path={actualPath}.");
        Assert.Equal("uploads/customer/part.stl",
            localPolicy.BuildFinalObjectName("Uploads\\Customer/", "PART.STL", uploadId));
    }

    /// <summary>Preserves padding in an explicit source-compatible custom upload prefix.</summary>
    /// <param name="path">The literal custom prefix whose spaces belong to the selected storage object.</param>
    /// <param name="expectedObjectName">The independently derived lower-case object name retaining the original prefix padding.</param>
    [Theory]
    [InlineData(" Uploads/Customer ", " uploads/customer /part.stl")]
    [InlineData("Uploads/Customer\u00a0", "uploads/customer\u00a0/part.stl")]
    public void BuildFinalObjectName_CustomPath_PreservesOriginalPadding(string path, string expectedObjectName)
    {
        var actual = policy.BuildFinalObjectName(path, "PART.STL", Guid.Empty);

        Assert.Equal(expectedObjectName, actual);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("uploads/../../escape")]
    [InlineData("uploads/./escape")]
    public void BuildFinalObjectName_Traversal_Rejects(string path)
    {
        Assert.Throws<FileUploadValidationException>(() => policy.BuildFinalObjectName(path, "part.stl", Guid.Empty));
    }

    /// <summary>Rejects traversal and raw controls before padding can become an upload object coordinate.</summary>
    /// <param name="path">The unsafe custom prefix that must fail the established upload safety checks.</param>
    [Theory]
    [InlineData("/")]
    [InlineData("\\\\")]
    [InlineData(" ../escape ")]
    [InlineData("\tUploads/Customer")]
    [InlineData("Uploads/Customer\n")]
    [InlineData("Uploads/\u0001/Customer")]
    public void BuildFinalObjectName_PaddedUnsafePath_Rejects(string path)
    {
        Assert.Throws<FileUploadValidationException>(() => policy.BuildFinalObjectName(path, "PART.STL", Guid.Empty));
    }

    /// <summary>Restores outer padding at the validated prefix boundary after leading separator cleanup exposes spaces.</summary>
    /// <param name="path">The custom prefix whose separator cleanup can shorten its validated form.</param>
    /// <param name="expectedObjectName">The destination retaining established separator normalization and any outer prefix padding.</param>
    [Theory]
    [InlineData("/          A", "          a/part.stl")]
    [InlineData("/ A ", " a /part.stl")]
    public void BuildFinalObjectName_SeparatorExposedPadding_UsesValidatedPrefixBoundary(string path, string expectedObjectName)
    {
        Assert.Equal(expectedObjectName, policy.BuildFinalObjectName(path, "PART.STL", Guid.Empty));
    }

    [Fact]
    public void RequireBucket_UnknownBucket_Rejects()
    {
        Assert.Throws<FileUploadValidationException>(() => policy.RequireBucket("attacker-bucket"));
    }
    private sealed class FixedLocalTimeProvider(DateTimeOffset instant, TimeSpan offset) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;

        public override TimeZoneInfo LocalTimeZone { get; } =
            TimeZoneInfo.CreateCustomTimeZone("fixture-local", offset, "fixture-local", "fixture-local");
    }
}
