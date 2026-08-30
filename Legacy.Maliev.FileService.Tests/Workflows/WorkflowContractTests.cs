using System.Text.RegularExpressions;

using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Legacy.Maliev.FileService.Tests.Workflows;

public sealed class WorkflowContractTests
{
    private static readonly string Workflow = File.ReadAllText(FindRepositoryFile(".github", "workflows", "_build-and-test.yml"));
    private static readonly string ApiProject = File.ReadAllText(
        FindRepositoryFile("Legacy.Maliev.FileService.Api", "Legacy.Maliev.FileService.Api.csproj"));

    [Fact]
    public void BuildAndTest_SatisfiesStructuralContract()
    {
        WorkflowContractValidator.Validate(Workflow);
    }

    [Fact]
    public void DependabotConfiguration_ScansOnlyIndependentlyResolvableProjectDirectories()
    {
        var source = File.ReadAllText(FindRepositoryFile(".github", "dependabot.yml"));
        var yaml = new YamlStream();
        yaml.Load(new StringReader(source));

        var root = Assert.IsType<YamlMappingNode>(Assert.Single(yaml.Documents).RootNode);
        var updates = Assert.IsType<YamlSequenceNode>(ReadNode(root, "updates"));
        var nuget = updates.Children
            .Select(Assert.IsType<YamlMappingNode>)
            .Single(update => ReadScalar(update, "package-ecosystem") == "nuget");
        var directories = Assert.IsType<YamlSequenceNode>(ReadNode(nuget, "directories"));

        Assert.Equal(
            [
                "/Legacy.Maliev.FileService.Application",
                "/Legacy.Maliev.FileService.Data",
                "/Legacy.Maliev.FileService.Domain",
            ],
            directories.Children.Select(Assert.IsType<YamlScalarNode>).Select(node => node.Value));
        Assert.False(nuget.Children.ContainsKey(new YamlScalarNode("directory")));
        Assert.False(nuget.Children.ContainsKey(new YamlScalarNode("exclude-paths")));
    }

    [Fact]
    public void DependabotConfiguration_AllowsCoordinatedEfUpdatesAndDefersSharedRuntimeAndRedisMajorUpdates()
    {
        var source = File.ReadAllText(FindRepositoryFile(".github", "dependabot.yml"));
        var yaml = new YamlStream();
        yaml.Load(new StringReader(source));

        var root = Assert.IsType<YamlMappingNode>(Assert.Single(yaml.Documents).RootNode);
        var updates = Assert.IsType<YamlSequenceNode>(ReadNode(root, "updates"));
        var nuget = updates.Children
            .Select(Assert.IsType<YamlMappingNode>)
            .Single(update => ReadScalar(update, "package-ecosystem") == "nuget");
        var ignored = Assert.IsType<YamlSequenceNode>(ReadNode(nuget, "ignore"));
        var rules = ignored.Children.Select(Assert.IsType<YamlMappingNode>).ToArray();

        Assert.Equal(
            [
                "Legacy.Maliev.ServiceDefaults",
                "Legacy.Maliev.CompatibilityContracts",
                "StackExchange.Redis",
            ],
            rules.Select(rule => ReadScalar(rule, "dependency-name")));
        var redis = rules.Single(rule => ReadScalar(rule, "dependency-name") == "StackExchange.Redis");
        var updateTypes = Assert.IsType<YamlSequenceNode>(ReadNode(redis, "update-types"));
        Assert.Equal(
            ["version-update:semver-major"],
            updateTypes.Children.Select(Assert.IsType<YamlScalarNode>).Select(node => node.Value));
    }

    [Fact]
    public void BuildAndTest_RejectsSharedActionMainWithPinnedShaComment()
    {
        AssertMutationRejected(
            "MALIEV-Co-Ltd/Legacy.Maliev.Workflows/actions/dotnet-validate@6017816fa67f369d785ed30794f002cfd6299af7",
            "MALIEV-Co-Ltd/Legacy.Maliev.Workflows/actions/dotnet-validate@main # 6017816fa67f369d785ed30794f002cfd6299af7");
    }

    [Fact]
    public void BuildAndTest_RejectsCommentedDependencySha()
    {
        AssertMutationRejected(
            "ref: 3152a9612d8514597192a98eae31277aef8102ff",
            "ref: main # 3152a9612d8514597192a98eae31277aef8102ff");
    }

    [Fact]
    public void ApiProject_UsesOnlyLegacyServiceDefaults()
    {
        Assert.Contains("Legacy.Maliev.ServiceDefaults", ApiProject, StringComparison.Ordinal);
        Assert.DoesNotContain("Maliev.Aspire\\Maliev.Aspire.ServiceDefaults", ApiProject, StringComparison.Ordinal);
        Assert.DoesNotContain("Include=\"Maliev.Aspire.ServiceDefaults\"", ApiProject, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildAndTest_RejectsJobPermissionEscalation()
    {
        AssertMutationRejected(
            "  validate:\n    name: validate",
            "  validate:\n    permissions:\n      contents: write\n    name: validate");
    }

    [Fact]
    public void BuildAndTest_RejectsSecretReferenceAnywhere()
    {
        var mutated = $"{Workflow}\n# ${{{{ secrets.X }}}}\n";

        Assert.Throws<InvalidOperationException>(() => WorkflowContractValidator.Validate(mutated));
    }

    [Theory]
    [InlineData("${{secrets.X}}")]
    [InlineData("${{ secrets['X'] }}")]
    public void BuildAndTest_RejectsSecretExpressionInJobEnvironment(string expression)
    {
        AssertMutationRejected(
            "    env:\n      MalievWorkspaceRoot: ${{ github.workspace }}/.dependencies",
            $"    env:\n      MalievWorkspaceRoot: ${{{{ github.workspace }}}}/.dependencies\n      REVIEW_TOKEN: {expression}");
    }

    [Fact]
    public void BuildAndTest_RejectsWhitespaceObfuscatedRestoreCommand()
    {
        AssertMutationRejected(
            "          solution: Legacy.Maliev.FileService.slnx",
            "          solution: Legacy.Maliev.FileService.slnx\n      - run: dotnet  restore Legacy.Maliev.FileService.slnx");
    }

    [Fact]
    public void BuildAndTest_RejectsMissingLocalDependencyOptIn()
    {
        AssertMutationRejected(
            "          use-local-maliev-dependencies: 'true'\n",
            string.Empty);
    }

    [Fact]
    public void BuildAndTest_RejectsReservedGitHubActionsOverride()
    {
        AssertMutationRejected(
            "          use-local-maliev-dependencies: 'true'\n",
            "          use-local-maliev-dependencies: 'true'\n        env:\n          GITHUB_ACTIONS: 'false'\n");
    }

    private static void AssertMutationRejected(string original, string replacement)
    {
        Assert.Contains(original, Workflow, StringComparison.Ordinal);
        var mutated = Workflow.Replace(original, replacement, StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(() => WorkflowContractValidator.Validate(mutated));
    }

    private static YamlNode ReadNode(YamlMappingNode mapping, string key)
    {
        return mapping.Children[new YamlScalarNode(key)];
    }

    private static string ReadScalar(YamlMappingNode mapping, string key)
    {
        return Assert.IsType<YamlScalarNode>(ReadNode(mapping, key)).Value
            ?? throw new InvalidOperationException($"Expected '{key}' to have a scalar value.");
    }

    private static string FindRepositoryFile(params string[] segments)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find repository file '{Path.Combine(segments)}'.");
    }
}

internal static partial class WorkflowContractValidator
{
    private const string CheckoutAction = "actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1";
    private const string SharedValidationAction = "MALIEV-Co-Ltd/Legacy.Maliev.Workflows/actions/dotnet-validate@6017816fa67f369d785ed30794f002cfd6299af7";

    public static void Validate(string workflow)
    {
        if (SecretExpression().IsMatch(workflow))
        {
            throw new InvalidOperationException("Workflow must not reference secrets.");
        }

        var yaml = new YamlStream();
        try
        {
            yaml.Load(new StringReader(workflow));
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException("Workflow must be valid YAML.", exception);
        }

        if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new InvalidOperationException("Workflow must contain exactly one mapping document.");
        }

        var workflowPermissions = RequireExactReadOnlyPermissions(RequireMapping(root, "permissions"), "workflow");
        var jobs = RequireMapping(root, "jobs");
        if (jobs.Children.Count != 1)
        {
            throw new InvalidOperationException("Workflow must define only the validate job.");
        }

        var validateJob = RequireMapping(jobs, "validate");
        var jobPermissionsNode = GetOptional(validateJob, "permissions");
        var effectiveJobPermissions = jobPermissionsNode is null
            ? workflowPermissions
            : RequireExactReadOnlyPermissions(RequireMapping(jobPermissionsNode, "jobs.validate.permissions"), "validate job");
        if (!effectiveJobPermissions.SequenceEqual(workflowPermissions, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Validate job permissions must not differ from workflow permissions.");
        }

        RequireScalarValue(validateJob, "name", "validate");
        RejectDuplicatedValidationActionsAndCommands(jobs);

        var steps = RequireSequence(validateJob, "steps");
        if (steps.Children.Count != 4)
        {
            throw new InvalidOperationException("Validate job must contain exactly four caller-owned steps.");
        }

        ValidateStep(
            steps.Children[0],
            CheckoutAction,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["persist-credentials"] = "false",
            });
        ValidateStep(
            steps.Children[1],
            CheckoutAction,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["repository"] = "MALIEV-Co-Ltd/Legacy.Maliev.ServiceDefaults",
                ["ref"] = "3152a9612d8514597192a98eae31277aef8102ff",
                ["path"] = ".dependencies/Legacy.Maliev.ServiceDefaults",
                ["persist-credentials"] = "false",
            });
        ValidateStep(
            steps.Children[2],
            CheckoutAction,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["repository"] = "MALIEV-Co-Ltd/Legacy.Maliev.CompatibilityContracts",
                ["ref"] = "78e48ffc4ee000df0510cba5e7c7a3c4c4d539d7",
                ["path"] = ".dependencies/Legacy.Maliev.CompatibilityContracts",
                ["persist-credentials"] = "false",
            });
        ValidateStep(
            steps.Children[3],
            SharedValidationAction,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["solution"] = "Legacy.Maliev.FileService.slnx",
                ["use-local-maliev-dependencies"] = "true",
            });
    }

    private static IReadOnlyList<string> RequireExactReadOnlyPermissions(YamlMappingNode permissions, string scope)
    {
        if (permissions.Children.Count != 1)
        {
            throw new InvalidOperationException($"{scope} permissions must contain only contents: read.");
        }

        RequireScalarValue(permissions, "contents", "read");
        return ["contents:read"];
    }

    private static void ValidateStep(
        YamlNode node,
        string expectedAction,
        IReadOnlyDictionary<string, string> expectedInputs)
    {
        var step = RequireMapping(node, "workflow step");
        var allowedKeys = new HashSet<string>(["name", "uses", "with"], StringComparer.Ordinal);

        var actualKeys = step.Children.Keys.Select(RequireScalar).ToHashSet(StringComparer.Ordinal);
        if (!actualKeys.SetEquals(allowedKeys))
        {
            throw new InvalidOperationException("Each workflow step must contain exactly name, uses, and with.");
        }

        var name = RequireScalar(GetRequired(step, "name"));
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("Workflow step name must not be empty.");
        }

        RequireScalarValue(step, "uses", expectedAction);
        var inputs = RequireMapping(step, "with");
        if (inputs.Children.Count != expectedInputs.Count)
        {
            throw new InvalidOperationException($"Action {expectedAction} has an unexpected input count.");
        }

        foreach (var expectedInput in expectedInputs)
        {
            RequireScalarValue(inputs, expectedInput.Key, expectedInput.Value);
        }

        var environment = GetOptional(step, "env");
        if (environment is not null)
        {
            throw new InvalidOperationException("Workflow action steps must not override reserved environment variables.");
        }

        if (expectedInputs.ContainsKey("use-local-maliev-dependencies"))
        {
            var useLocalDependencies = GetRequired(inputs, "use-local-maliev-dependencies") as YamlScalarNode;
            if (useLocalDependencies?.Style != ScalarStyle.SingleQuoted)
            {
                throw new InvalidOperationException("use-local-maliev-dependencies must use the single-quoted string value 'true'.");
            }
        }
    }

    private static void RejectDuplicatedValidationActionsAndCommands(YamlMappingNode jobs)
    {
        foreach (var jobNode in jobs.Children.Values.OfType<YamlMappingNode>())
        {
            var stepsNode = GetOptional(jobNode, "steps");
            if (stepsNode is not YamlSequenceNode steps)
            {
                continue;
            }

            foreach (var stepNode in steps.Children.OfType<YamlMappingNode>())
            {
                if (GetOptional(stepNode, "uses") is YamlScalarNode usesNode)
                {
                    var action = usesNode.Value ?? string.Empty;
                    if (action.StartsWith("actions/setup-dotnet@", StringComparison.OrdinalIgnoreCase)
                        || action.StartsWith("actions/cache@", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException($"Caller duplicates shared action {action}.");
                    }
                }

                if (GetOptional(stepNode, "run") is YamlScalarNode runNode)
                {
                    RejectDuplicatedDotNetCommand(runNode.Value ?? string.Empty);
                }
            }
        }
    }

    private static void RejectDuplicatedDotNetCommand(string command)
    {
        var tokens = command
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => token.Trim('"', '\'', ';', '&', '|').ToLowerInvariant())
            .Where(token => token.Length > 0)
            .ToArray();

        for (var index = 0; index < tokens.Length - 1; index++)
        {
            if (!string.Equals(tokens[index], "dotnet", StringComparison.Ordinal))
            {
                continue;
            }

            var verb = tokens[index + 1];
            if (verb is "restore" or "build" or "test" or "format" or "list"
                || tokens[(index + 1)..].Any(token => token is "audit" or "--vulnerable"))
            {
                throw new InvalidOperationException($"Caller duplicates shared dotnet validation command: {verb}.");
            }
        }
    }

    private static YamlMappingNode RequireMapping(YamlMappingNode parent, string key) =>
        RequireMapping(GetRequired(parent, key), key);

    private static YamlMappingNode RequireMapping(YamlNode node, string description)
    {
        return node as YamlMappingNode
            ?? throw new InvalidOperationException($"{description} must be a mapping.");
    }

    private static YamlSequenceNode RequireSequence(YamlMappingNode parent, string key)
    {
        return GetRequired(parent, key) as YamlSequenceNode
            ?? throw new InvalidOperationException($"{key} must be a sequence.");
    }

    private static void RequireScalarValue(YamlMappingNode parent, string key, string expected)
    {
        var actual = RequireScalar(GetRequired(parent, key));
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{key} must equal '{expected}', but was '{actual}'.");
        }
    }

    private static string RequireScalar(YamlNode node)
    {
        return (node as YamlScalarNode)?.Value
            ?? throw new InvalidOperationException("Expected a scalar YAML value.");
    }

    private static YamlNode GetRequired(YamlMappingNode parent, string key)
    {
        return GetOptional(parent, key)
            ?? throw new InvalidOperationException($"Missing required YAML key '{key}'.");
    }

    private static YamlNode? GetOptional(YamlMappingNode parent, string key)
    {
        foreach (var child in parent.Children)
        {
            if (child.Key is YamlScalarNode scalar && string.Equals(scalar.Value, key, StringComparison.Ordinal))
            {
                return child.Value;
            }
        }

        return null;
    }

    [GeneratedRegex(@"\$\{\{\s*secrets\s*(?:\.|\[)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretExpression();
}
