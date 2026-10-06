using System.Diagnostics;
using Legacy.Maliev.FileService.Tests.Api;

namespace Legacy.Maliev.FileService.Tests.Workflows;

[Collection(FileIncidentHttpCollection.Name)]
public sealed class FileIdentityResourceApplicationContractTests
{
    [Theory]
    [InlineData("success-explicit")]
    [InlineData("success-inherited")]
    [InlineData("callback-copy")]
    [InlineData("no-recording")]
    [InlineData("no-adapter")]
    [InlineData("pod-false")]
    [InlineData("account-false")]
    [InlineData("iam-rejected")]
    [InlineData("iam-warning")]
    [InlineData("image-fails")]
    [InlineData("image-warning")]
    [InlineData("source-before-dirty")]
    [InlineData("ksa-rejected")]
    [InlineData("ksa-throws")]
    [InlineData("ksa-unknown")]
    [InlineData("ksa-wrong-identity")]
    [InlineData("ksa-string-status")]
    [InlineData("ksa-multiple")]
    [InlineData("ksa-warning")]
    [InlineData("ksa-error")]
    [InlineData("deployment-rejected")]
    [InlineData("deployment-throws")]
    [InlineData("source-after-ksa-dirty")]
    [InlineData("source-after-ksa-remote")]
    [InlineData("producer-after-ksa-drift")]
    [InlineData("module-after-ksa-drift")]
    [InlineData("source-after-deployment-dirty")]
    [InlineData("plan-arguments-invalid")]
    public async Task RecordingBoundary_EnforcesTokenIntentSourceAndKsaBeforeDeployment(string scenario)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Legacy.Maliev.FileService.slnx")))
            root = root.Parent;
        Assert.NotNull(root);
        var start = new ProcessStartInfo("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = root.FullName,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(Path.Combine(root.FullName, "Legacy.Maliev.FileService.Tests", "Workflows", "FileIdentityResourceApplicationScenarios.ps1"));
        start.ArgumentList.Add("-Scenario");
        start.ArgumentList.Add(scenario);
        using var process = Process.Start(start);
        Assert.NotNull(process);
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        Assert.Equal(0, process.ExitCode);
        Assert.Equal("PASS:" + scenario, (await output).Trim());
        Assert.Empty(await error);
    }
}
