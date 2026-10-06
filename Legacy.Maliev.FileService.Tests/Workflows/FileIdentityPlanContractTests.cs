using System.Diagnostics;
using Legacy.Maliev.FileService.Tests.Api;
using YamlDotNet.Serialization;

namespace Legacy.Maliev.FileService.Tests.Workflows;

[Collection(FileIncidentHttpCollection.Name)]
public sealed class FileIdentityPlanContractTests
{
    [Theory]
    [InlineData("intent-valid")]
    [InlineData("intent-namespace")]
    [InlineData("intent-deployment")]
    [InlineData("intent-container")]
    [InlineData("intent-ksa")]
    [InlineData("intent-account-namespace")]
    [InlineData("intent-account-name")]
    [InlineData("intent-gsa")]
    [InlineData("intent-token-false")]
    [InlineData("intent-token-string")]
    [InlineData("iam-valid")]
    [InlineData("iam-project")]
    [InlineData("iam-bucket")]
    [InlineData("iam-gsa")]
    [InlineData("iam-missing-object")]
    [InlineData("iam-missing-wif")]
    [InlineData("iam-missing-signing")]
    [InlineData("iam-conditional-object")]
    [InlineData("iam-conditional-wif")]
    [InlineData("iam-conditional-signing")]
    [InlineData("iam-storage-admin")]
    [InlineData("iam-owner")]
    [InlineData("iam-editor")]
    [InlineData("iam-member-case")]
    [InlineData("iam-duplicate-binding")]
    [InlineData("iam-public")]
    [InlineData("iam-cross-bucket")]
    [InlineData("iam-cross-signing")]
    [InlineData("iam-cross-wif")]
    [InlineData("plan-fixture-without-opt-in")]
    [InlineData("plan-production-origin-mismatch")]
    [InlineData("plan-source-dirty-tracked")]
    [InlineData("plan-source-dirty-untracked")]
    [InlineData("plan-source-dirty-staged")]
    [InlineData("plan-source-wrong-head")]
    [InlineData("plan-source-origin")]
    [InlineData("plan-source-stale-remote")]
    [InlineData("plan-reader-source-dirty")]
    [InlineData("plan-reader-source-remote")]
    [InlineData("plan-builder-source-dirty")]
    [InlineData("plan-builder-source-remote")]
    [InlineData("plan-source-helper-missing")]
    [InlineData("plan-source-helper-changed")]
    [InlineData("plan-source-module-changed")]
    [InlineData("plan-valid")]
    [InlineData("plan-duplicate-json")]
    [InlineData("plan-duplicate-iam-json")]
    [InlineData("plan-helper-missing")]
    [InlineData("plan-helper-changed")]
    [InlineData("plan-identity-invalid")]
    [InlineData("plan-reader-fails")]
    [InlineData("plan-policy-invalid")]
    [InlineData("plan-builder-fails")]
    [InlineData("plan-image-invalid")]
    [InlineData("plan-source-invalid")]
    [InlineData("plan-unapproved")]
    [InlineData("plan-metadata-http")]
    [InlineData("plan-metadata-tcp")]
    [InlineData("plan-metadata-grpc")]
    [InlineData("plan-metadata-references")]
    [InlineData("plan-metadata-singleton")]
    [InlineData("plan-metadata-literal")]
    [InlineData("plan-metadata-selected-template")]
    [InlineData("plan-metadata-probe-exec")]
    [InlineData("plan-metadata-probe-headers")]
    [InlineData("plan-metadata-probe-unknown")]
    [InlineData("plan-metadata-probe-multiple")]
    [InlineData("plan-metadata-probe-missing-port")]
    [InlineData("plan-metadata-probe-scalar")]
    [InlineData("plan-metadata-reference-unknown")]
    [InlineData("plan-metadata-reference-scalar")]
    [InlineData("plan-metadata-reference-multiple")]
    [InlineData("plan-metadata-reference-null")]
    [InlineData("plan-metadata-environment-scalar")]
    [InlineData("plan-metadata-environment-entry-scalar")]
    public async Task OfflineIdentityAndIamContract_UsesActualParsedProducer(string scenario)
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
        start.ArgumentList.Add(Path.Combine(root.FullName, "Legacy.Maliev.FileService.Tests", "Workflows", "FileIdentityPlanScenarios.ps1"));
        start.ArgumentList.Add("-Scenario");
        start.ArgumentList.Add(scenario);
        if (scenario == "plan-metadata-selected-template")
        {
            var template = File.ReadAllText(Path.Combine(root.FullName, "deploy", "disabled", "file-deployment.template.yaml"));
            var parsed = new DeserializerBuilder().WithAttemptingUnquotedStringTypeDeserialization().Build().Deserialize<object>(template);
            var json = new SerializerBuilder().JsonCompatible().Build().Serialize(parsed);
            start.ArgumentList.Add("-SelectedDeploymentJson");
            start.ArgumentList.Add(json);
        }
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
