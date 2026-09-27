using System.Net.Http.Headers;
using Legacy.Maliev.FileService.Data;

namespace Legacy.Maliev.FileService.Tests.Data;

public sealed class GoogleCloudObjectStorageSignedUrlTests
{
    [Theory]
    [InlineData("orders/123/part.stl", "part.stl")]
    [InlineData(@"orders\123\part.stl", "part.stl")]
    [InlineData("orders/123/ค่าทำสี.stl", "ค่าทำสี.stl")]
    public void ReadRequest_UsesOnlyBasenameAsAttachmentFilename(string objectName, string expectedFileName)
    {
        var request = GoogleCloudObjectStorage.CreateReadRequestTemplate("private-bucket", objectName);

        Assert.Equal("private-bucket", request.Bucket);
        Assert.Equal(objectName, request.ObjectName);
        Assert.Equal(HttpMethod.Get, request.HttpMethod);
        var value = Assert.Single(request.QueryParameters["response-content-disposition"]);
        Assert.True(ContentDispositionHeaderValue.TryParse(value, out var disposition));
        Assert.Equal("attachment", disposition.DispositionType);
        Assert.Equal(expectedFileName, disposition.FileNameStar);
        Assert.DoesNotContain("orders", value, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadRequest_EncodesHostileFilenameWithoutHeaderInjection()
    {
        var request = GoogleCloudObjectStorage.CreateReadRequestTemplate(
            "private-bucket", "orders/123/evil\r\nX-Injected: yes\".stl");

        var value = Assert.Single(request.QueryParameters["response-content-disposition"]);
        Assert.DoesNotContain('\r', value);
        Assert.DoesNotContain('\n', value);
        Assert.True(ContentDispositionHeaderValue.TryParse(value, out var disposition));
        Assert.Equal("evil__X-Injected: yes_.stl", disposition.FileNameStar);
        Assert.Equal("evil__X-Injected: yes_.stl", disposition.FileName?.Trim('"'));
    }
}
