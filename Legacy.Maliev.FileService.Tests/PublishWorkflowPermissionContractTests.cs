using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace Legacy.Maliev.FileService.Tests;

public sealed class PublishWorkflowPermissionContractTests
{
    [Fact]
    public void Publisher_UsesReviewedValidationProducer()
    {
        Assert.Equal("MALIEV-Co-Ltd/Legacy.Maliev.Workflows/.github/workflows/publish-image.yml@503e8846390a597c267d2889b33a9c26863389b3",
            Scalar(Publish(Parse()), "uses"));
    }

    [Fact]
    public void Publisher_PreservesMinimalPermissionsInputsAndAdmissionGates() => Validate(Parse());

    [Theory]
    [InlineData("contents", "write")]
    [InlineData("actions", "write")]
    [InlineData("id-token", "read")]
    [InlineData("contents", "")]
    [InlineData("actions", "")]
    [InlineData("id-token", "")]
    [InlineData("packages", "write")]
    public void Publisher_RejectsMissingElevatedOrAdditionalPermissions(string name, string value)
    {
        var root = Parse();
        Validate(root);
        var permissions = Mapping(Publish(root), "permissions");
        if (value.Length == 0) permissions.Children.Remove(new YamlScalarNode(name));
        else permissions.Children[new YamlScalarNode(name)] = new YamlScalarNode(value);
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => Validate(root));
    }

    [Theory]
    [InlineData("image", "${{ vars.LEGACY_ARTIFACT_REGISTRY }}/maliev-file-service")]
    [InlineData("dockerfile", "Maliev.FileService.Api/Dockerfile")]
    [InlineData("context", "..")]
    [InlineData("environment", "production")]
    [InlineData("workload-identity-provider", "")]
    [InlineData("service-account", "${{ vars.GENERIC_PUBLISHER_SERVICE_ACCOUNT }}")]
    [InlineData("legacy-service-defaults-ref", "main")]
    [InlineData("compatibility-contracts-ref", "main")]
    public void Publisher_RejectsChangedAuthorityIdentityAndUnpinnedDependencies(string input, string unsafeValue)
    {
        var root = Parse();
        Validate(root);
        Mapping(Publish(root), "with").Children[new YamlScalarNode(input)] = new YamlScalarNode(unsafeValue);

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => Validate(root));
    }

    [Theory]
    [InlineData("publish", "true")]
    [InlineData("publish", "vars.LEGACY_DEPLOY_ENABLED != 'false'")]
    [InlineData("deployment-gate", "vars.LEGACY_DEPLOY_ENABLED == 'false'")]
    [InlineData("deployment-gate", "false")]
    public void Publisher_RejectsWeakOrNoncomplementaryActivationConditions(string jobName, string unsafeCondition)
    {
        var root = Parse();
        Validate(root);
        Mapping(Mapping(root, "jobs"), jobName).Children[new YamlScalarNode("if")] = new YamlScalarNode(unsafeCondition);

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => Validate(root));
    }

    private static void Validate(YamlMappingNode root)
    {
        var global = Mapping(root, "permissions");
        Assert.Single(global.Children);
        Assert.Equal("read", Scalar(global, "contents"));
        var publish = Publish(root);
        var permissions = Mapping(publish, "permissions");
        Assert.Equal(3, permissions.Children.Count);
        Assert.Equal("read", Scalar(permissions, "contents"));
        Assert.Equal("read", Scalar(permissions, "actions"));
        Assert.Equal("write", Scalar(permissions, "id-token"));
        Assert.Equal("vars.LEGACY_DEPLOY_ENABLED == 'true'", Scalar(publish, "if"));
        var gate = Mapping(Mapping(root, "jobs"), "deployment-gate");
        Assert.Equal("vars.LEGACY_DEPLOY_ENABLED != 'true'", Scalar(gate, "if"));
        Assert.False(gate.Children.ContainsKey(new YamlScalarNode("permissions")));
        var inputs = Mapping(publish, "with");
        Assert.Equal(8, inputs.Children.Count);
        Assert.Equal("${{ vars.LEGACY_ARTIFACT_REGISTRY }}/legacy-maliev-file-service", Scalar(inputs, "image"));
        Assert.Equal("Legacy.Maliev.FileService.Api/Dockerfile", Scalar(inputs, "dockerfile"));
        Assert.Equal(".", Scalar(inputs, "context"));
        Assert.Equal("legacy-production", Scalar(inputs, "environment"));
        Assert.Equal("${{ vars.LEGACY_WORKLOAD_IDENTITY_PROVIDER }}", Scalar(inputs, "workload-identity-provider"));
        Assert.Equal("${{ vars.LEGACY_FILE_PUBLISHER_SERVICE_ACCOUNT }}", Scalar(inputs, "service-account"));
        Assert.Equal("8f4f5f27b226ffe406c4c79b1903742e8c2e7dd3", Scalar(inputs, "legacy-service-defaults-ref"));
        Assert.Equal("78e48ffc4ee000df0510cba5e7c7a3c4c4d539d7", Scalar(inputs, "compatibility-contracts-ref"));
    }

    private static YamlMappingNode Parse()
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(File.ReadAllText(Path.Combine(FindRoot(), ".github", "workflows", "publish-image.yml"))));
        return Assert.IsType<YamlMappingNode>(Assert.Single(yaml.Documents).RootNode);
    }
    private static YamlMappingNode Mapping(YamlMappingNode parent, string key) =>
        Assert.IsType<YamlMappingNode>(parent.Children[new YamlScalarNode(key)]);
    private static YamlMappingNode Publish(YamlMappingNode root) => Mapping(Mapping(root, "jobs"), "publish");
    private static string? Scalar(YamlMappingNode parent, string key) =>
        parent.Children.TryGetValue(new YamlScalarNode(key), out var node) ? Assert.IsType<YamlScalarNode>(node).Value : null;

    [Fact]
    public void PublishWorkflow_ScopesOidcToPublishJobs()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRoot(),
            ".github",
            "workflows",
            "publish-image.yml"));

        var jobsIndex = source.IndexOf("\njobs:", StringComparison.Ordinal);
        Assert.True(jobsIndex > 0, "The publish workflow must define a jobs section.");
        var workflowHeader = source[..jobsIndex];
        Assert.Contains("permissions:", workflowHeader, StringComparison.Ordinal);
        Assert.Contains("  contents: read", workflowHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("id-token: write", workflowHeader, StringComparison.OrdinalIgnoreCase);

        var publishJobs = Regex.Matches(
            source,
            @"(?ms)^  publish(?:-[^:\r\n]+)?:\r?\n(?<body>.*?)(?=^  [A-Za-z0-9_-]+:\s*\r?$|\z)");
        Assert.NotEmpty(publishJobs);
        foreach (Match publishJob in publishJobs)
        {
            var job = publishJob.Groups["body"].Value;
            Assert.Contains("permissions:", job, StringComparison.Ordinal);
            Assert.Contains("contents: read", job, StringComparison.Ordinal);
            Assert.Contains("id-token: write", job, StringComparison.Ordinal);
        }

        var deploymentGate = Regex.Match(
            source,
            @"(?ms)^  deployment-gate:\r?\n(?<body>.*?)(?=^  [A-Za-z0-9_-]+:\s*\r?$|\z)");
        if (deploymentGate.Success)
        {
            Assert.DoesNotContain("id-token: write", deploymentGate.Groups["body"].Value, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var workflow = Path.Combine(directory.FullName, ".github", "workflows", "publish-image.yml");
            if (File.Exists(workflow))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("The publish workflow root was not found.");
    }
}
