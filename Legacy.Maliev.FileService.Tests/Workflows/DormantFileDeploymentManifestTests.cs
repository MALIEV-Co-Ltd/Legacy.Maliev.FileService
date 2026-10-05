using System.Diagnostics;
using System.Text.Json;
using YamlDotNet.RepresentationModel;

namespace Legacy.Maliev.FileService.Tests.Workflows;

public sealed class DormantFileDeploymentManifestTests
{
    // Synthetic digest only; these tests do not prove that this image was built or published.
    private const string FixtureRepository = "asia-southeast1-docker.pkg.dev/maliev-website/maliev-website-artifact-prod/legacy-maliev-file-service";
    private static readonly string FixtureImage = FixtureRepository + "@sha256:" + new string('1', 64);

    [Fact]
    public async Task ActualRenderer_RequiresExactExplicitRepositoryApproval()
    {
        var result = await RunScriptAsync(Path.Combine(RepositoryRoot(), "scripts", "Render-DormantLegacyFileDeployment.ps1"),
            ["-Image", FixtureImage, "-ApprovedImageRepository", FixtureRepository.Replace("maliev-website-artifact-prod", "other-legacy-repository", StringComparison.Ordinal)]);
        Assert.NotEqual(0, result.Exit);
        Assert.Empty(result.Output);
        Assert.Contains("An approved immutable legacy File image digest is required.", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task ActualRenderer_RejectsTrailingLineEndingInRepositoryApproval(string ending)
    {
        var result = await RunScriptAsync(Path.Combine(RepositoryRoot(), "scripts", "Render-DormantLegacyFileDeployment.ps1"),
            ["-Image", FixtureImage, "-ApprovedImageRepository", FixtureRepository + ending]);
        Assert.NotEqual(0, result.Exit);
        Assert.Empty(result.Output);
        Assert.Contains("An approved immutable legacy File image digest is required.", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ActualRenderedManifestAndExistingKsa_ActualIdentityGuardRemainsBlocked()
    {
        var rendered = await RenderAsync(FixtureImage);
        Assert.Equal(0, rendered.Exit);
        Assert.Empty(rendered.Error);
        var account = Parse(await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "Legacy.Maliev.FileService.Tests", "Workflows", "fixtures", "existing-file-service-account.yaml")));
        var result = await RunScriptAsync(Path.Combine(RepositoryRoot(), "Legacy.Maliev.FileService.Tests", "Workflows", "DormantFileIdentityMismatchScenario.ps1"),
            ["-DeploymentJson", JsonSerializer.Serialize(YamlValue(Parse(rendered.Output))), "-ServiceAccountJson", JsonSerializer.Serialize(YamlValue(account))]);
        Assert.Equal(0, result.Exit);
        Assert.Empty(result.Error);
        Assert.Equal("BLOCKED:existing-token-intent", result.Output);
    }

    [Fact]
    public async Task ActualRenderer_ProducesDormantDeploymentWithExistingIdentityAndManagedSecret()
    {
        var result = await RenderAsync(FixtureImage);
        Assert.Equal(0, result.Exit);
        Assert.Empty(result.Error);
        var root = Parse(result.Output);
        Assert.Equal("apps/v1", Scalar(root, "apiVersion"));
        Assert.Equal("Deployment", Scalar(root, "kind"));
        var metadata = Mapping(root, "metadata");
        Assert.Equal("legacy-maliev-file", Scalar(metadata, "name"));
        Assert.Equal("maliev-legacy", Scalar(metadata, "namespace"));
        Assert.Equal("false", Scalar(Mapping(metadata, "annotations"), "legacy.maliev.com/deployment-enabled"));
        var spec = Mapping(root, "spec");
        Assert.Equal("0", Scalar(spec, "replicas"));
        var template = Mapping(spec, "template");
        Assert.Equal(Scalar(Mapping(Mapping(spec, "selector"), "matchLabels"), "app.kubernetes.io/name"),
            Scalar(Mapping(Mapping(template, "metadata"), "labels"), "app.kubernetes.io/name"));
        var pod = Mapping(template, "spec");
        Assert.Equal("legacy-maliev-file", Scalar(pod, "serviceAccountName"));
        Assert.Equal("false", Scalar(pod, "automountServiceAccountToken"));
        var container = Container(root);
        Assert.Equal("legacy-maliev-file-service", Scalar(container, "name"));
        Assert.Equal(FixtureImage, Scalar(container, "image"));
        var reference = Mapping(Assert.IsType<YamlMappingNode>(Assert.Single(Sequence(container, "envFrom").Children)), "secretRef");
        Assert.Equal("legacy-maliev-file-runtime", Scalar(reference, "name"));
        Assert.Equal("false", Scalar(reference, "optional"));
        Assert.DoesNotContain("__REQUIRED_", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ActualRenderer_PreservesDisabledMutationFlagsAndCurrentPortAndProbes()
    {
        var result = await RenderAsync(FixtureImage);
        Assert.Equal(0, result.Exit);
        Assert.Empty(result.Error);
        var container = Container(Parse(result.Output));
        var environment = Sequence(container, "env").Children.Cast<YamlMappingNode>()
            .ToDictionary(item => Scalar(item, "name"), item => Scalar(item, "value"));
        Assert.Equal("Production", environment["ASPNETCORE_ENVIRONMENT"]);
        Assert.Equal("8080", environment["ASPNETCORE_HTTP_PORTS"]);
        foreach (var name in new[] { "FileStorage__Enabled", "FileStorage__WritesEnabled", "InstantQuoteFiles__Enabled", "InstantQuoteFiles__WritesEnabled", "InstantQuoteFiles__CleanupEnabled" })
            Assert.Equal("false", environment[name]);
        Assert.DoesNotContain(environment.Keys, key => key.StartsWith("ConnectionStrings", StringComparison.Ordinal) || key.StartsWith("Jwt", StringComparison.Ordinal));
        var port = Assert.IsType<YamlMappingNode>(Assert.Single(Sequence(container, "ports").Children));
        Assert.Equal("http", Scalar(port, "name"));
        Assert.Equal("8080", Scalar(port, "containerPort"));
        foreach (var name in new[] { "startupProbe", "livenessProbe", "readinessProbe" })
        {
            var http = Mapping(Mapping(container, name), "httpGet");
            Assert.Equal(name == "readinessProbe" ? "/file/readiness" : "/file/liveness", Scalar(http, "path"));
            Assert.Equal("http", Scalar(http, "port"));
        }
        var program = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "Legacy.Maliev.FileService.Api", "Program.cs"));
        var docker = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "Legacy.Maliev.FileService.Api", "Dockerfile"));
        Assert.Contains("app.MapDefaultEndpoints(\"file\")", program, StringComparison.Ordinal);
        Assert.Contains("EXPOSE 8080", docker, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ActualRenderer_RetainsSourceResourceBoundsAndNonRootTemporaryVolumePolicy()
    {
        var result = await RenderAsync(FixtureImage);
        Assert.Equal(0, result.Exit);
        Assert.Empty(result.Error);
        var root = Parse(result.Output);
        var container = Container(root);
        var resources = Mapping(container, "resources");
        Assert.Equal("50m", Scalar(Mapping(resources, "requests"), "cpu"));
        Assert.Equal("256Mi", Scalar(Mapping(resources, "requests"), "memory"));
        Assert.Equal("200m", Scalar(Mapping(resources, "limits"), "cpu"));
        Assert.Equal("512Mi", Scalar(Mapping(resources, "limits"), "memory"));
        var security = Mapping(container, "securityContext");
        Assert.Equal("true", Scalar(security, "runAsNonRoot"));
        Assert.Equal("1654", Scalar(security, "runAsUser"));
        Assert.Equal("true", Scalar(security, "readOnlyRootFilesystem"));
        Assert.Equal("false", Scalar(security, "allowPrivilegeEscalation"));
        Assert.Equal("ALL", Assert.IsType<YamlScalarNode>(Assert.Single(Sequence(Mapping(security, "capabilities"), "drop").Children)).Value);
        var mount = Assert.IsType<YamlMappingNode>(Assert.Single(Sequence(container, "volumeMounts").Children));
        Assert.Equal("/tmp", Scalar(mount, "mountPath"));
        var pod = Mapping(Mapping(Mapping(root, "spec"), "template"), "spec");
        Assert.Equal("1654", Scalar(Mapping(pod, "securityContext"), "fsGroup"));
        Assert.Equal("OnRootMismatch", Scalar(Mapping(pod, "securityContext"), "fsGroupChangePolicy"));
        var volume = Assert.IsType<YamlMappingNode>(Assert.Single(Sequence(pod, "volumes").Children));
        Assert.Equal(Scalar(mount, "name"), Scalar(volume, "name"));
        Assert.Equal("128Mi", Scalar(Mapping(volume, "emptyDir"), "sizeLimit"));
    }

    [Theory]
    [InlineData("asia-southeast1-docker.pkg.dev/maliev-website/maliev-website-artifact-prod/legacy-maliev-file-service:latest")]
    [InlineData("asia-southeast1-docker.pkg.dev/maliev-website/maliev-website-artifact-prod/legacy-maliev-file-service@sha256:short")]
    [InlineData("wrong-registry.example/legacy-maliev-file-service@sha256:")]
    [InlineData("asia-southeast1-docker.pkg.dev/maliev-website/maliev-website-artifact-prod/maliev-file-service@sha256:")]
    [InlineData("zero")]
    [InlineData("uppercase")]
    [InlineData("trailing-space")]
    [InlineData("newline")]
    [InlineData("crlf")]
    [InlineData("newline-injection")]
    public async Task ActualRenderer_RejectsMutableUnapprovedOrPlaceholderImagesWithoutManifest(string image)
    {
        image = image switch
        {
            "zero" => FixtureImage[..^64] + new string('0', 64),
            "uppercase" => FixtureImage[..^64] + new string('A', 64),
            "trailing-space" => FixtureImage + " ",
            "newline" => FixtureImage + "\n",
            "crlf" => FixtureImage + "\r\n",
            "newline-injection" => FixtureImage + "\nkind: Secret",
            _ when image.EndsWith(':') => image + new string('1', 64),
            _ => image,
        };
        var result = await RenderAsync(image);
        Assert.NotEqual(0, result.Exit);
        Assert.Empty(result.Output);
        Assert.Contains("An approved immutable legacy File image digest is required.", result.Error, StringComparison.Ordinal);
    }

    private static YamlMappingNode Parse(string text)
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(text));
        return Assert.IsType<YamlMappingNode>(Assert.Single(yaml.Documents).RootNode);
    }

    private static YamlMappingNode Mapping(YamlMappingNode node, string key) => Assert.IsType<YamlMappingNode>(node.Children[new YamlScalarNode(key)]);
    private static YamlSequenceNode Sequence(YamlMappingNode node, string key) => Assert.IsType<YamlSequenceNode>(node.Children[new YamlScalarNode(key)]);
    private static string Scalar(YamlMappingNode node, string key) => Assert.IsType<YamlScalarNode>(node.Children[new YamlScalarNode(key)]).Value!;
    private static YamlMappingNode Container(YamlMappingNode root) => Assert.IsType<YamlMappingNode>(Assert.Single(
        Sequence(Mapping(Mapping(Mapping(root, "spec"), "template"), "spec"), "containers").Children));

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Legacy.Maliev.FileService.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        return root.FullName;
    }

    private static object? YamlValue(YamlNode node) => node switch
    {
        YamlMappingNode mapping => mapping.Children.ToDictionary(item => Assert.IsType<YamlScalarNode>(item.Key).Value!, item => YamlValue(item.Value)),
        YamlSequenceNode sequence => sequence.Children.Select(YamlValue).ToArray(),
        YamlScalarNode scalar when bool.TryParse(scalar.Value, out var boolean) => boolean,
        YamlScalarNode scalar when int.TryParse(scalar.Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) => number,
        YamlScalarNode scalar => scalar.Value,
        _ => throw new InvalidOperationException("Unexpected manifest node."),
    };

    private static Task<(int Exit, string Output, string Error)> RenderAsync(string image) =>
        RunScriptAsync(Path.Combine(RepositoryRoot(), "scripts", "Render-DormantLegacyFileDeployment.ps1"), ["-Image", image, "-ApprovedImageRepository", FixtureRepository]);

    private static async Task<(int Exit, string Output, string Error)> RunScriptAsync(string script, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo("pwsh")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = RepositoryRoot() };
        foreach (var argument in new[] { "-NoProfile", "-File", script }.Concat(arguments))
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start);
        Assert.NotNull(process);
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        return (process.ExitCode, await output, await error);
    }
}
