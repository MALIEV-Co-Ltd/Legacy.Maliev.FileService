using Legacy.Maliev.FileService.Application.Models;
using Legacy.Maliev.FileService.Data;

namespace Legacy.Maliev.FileService.Tests.Data;

public sealed class ClamAvFileSafetyScannerTests
{
    [Fact]
    public void ParseResponse_Ok_ReturnsClean()
    {
        var result = ClamAvFileSafetyScanner.ParseResponse("stream: OK\0");

        Assert.Equal(FileSafetyVerdict.Clean, result.Verdict);
    }

    [Fact]
    public void ParseResponse_Found_ReturnsThreat()
    {
        var result = ClamAvFileSafetyScanner.ParseResponse("stream: Eicar-Test-Signature FOUND\0");

        Assert.Equal(FileSafetyVerdict.Infected, result.Verdict);
        Assert.Equal("Eicar-Test-Signature", result.ThreatName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("stream: Size limit exceeded. ERROR\0")]
    public void ParseResponse_Unknown_ReturnsUnavailable(string response)
    {
        var result = ClamAvFileSafetyScanner.ParseResponse(response);

        Assert.Equal(FileSafetyVerdict.Unavailable, result.Verdict);
    }
}
