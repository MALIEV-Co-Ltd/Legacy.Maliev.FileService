using System.Text.RegularExpressions;

using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Legacy.Maliev.FileService.Tests.Workflows;

public sealed class WorkflowContractTests
{
    private static readonly string Workflow = File.ReadAllText(FindRepositoryFile(".github", "workflows", "_build-and-test.yml"));
    private static readonly string ApiProject = File.ReadAllText(
        FindRepositoryFile("Legacy.Maliev.FileService.Api", "Legacy.Maliev.FileService.Api.csproj"));
    private static readonly string DataProject = File.ReadAllText(
        FindRepositoryFile("Legacy.Maliev.FileService.Data", "Legacy.Maliev.FileService.Data.csproj"));

    [Fact]
    public void BuildAndTest_SatisfiesStructuralContract()
    {
        WorkflowContractValidator.Validate(Workflow);
    }

    [Theory]
    [InlineData("ref: 3f5f7542c93cb085757130971c4fc7cf61043f01", "ref: main")]
    [InlineData("path: .dependencies/Legacy.Maliev.Intranet", "path: .dependencies/unreviewed")]
    [InlineData("98a4b2954e9dede6c3d237af1ae9b42198dec7f61835af22a61e601ded731bea", "0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("a4a31a998cc168709d0914fa19b4836f5da219c73a3d6f99c7a2e40d15429758", "0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("sha256sum --check --strict", "true")]
    public void BuildAndTest_RejectsChangedStrictConsumerOrMissingPrecompileHash(string original, string replacement)
    {
        Assert.Throws<InvalidOperationException>(() => WorkflowContractValidator.Validate(Workflow.Replace(original, replacement, StringComparison.Ordinal)));
    }

    [Fact]
    public void GatedImagePublish_UsesTheValidatedImmutableDependencyCommits()
    {
        var validation = new YamlStream();
        validation.Load(new StringReader(Workflow));
        var validationRoot = Assert.IsType<YamlMappingNode>(Assert.Single(validation.Documents).RootNode);
        var validationJobs = Assert.IsType<YamlMappingNode>(ReadNode(validationRoot, "jobs"));
        var validate = Assert.IsType<YamlMappingNode>(ReadNode(validationJobs, "validate"));
        var steps = Assert.IsType<YamlSequenceNode>(ReadNode(validate, "steps"));
        var serviceDefaultsCheckout = Assert.IsType<YamlMappingNode>(steps.Children[1]);
        var compatibilityCheckout = Assert.IsType<YamlMappingNode>(steps.Children[2]);
        var serviceDefaultsRef = ReadScalar(Assert.IsType<YamlMappingNode>(ReadNode(serviceDefaultsCheckout, "with")), "ref");
        var compatibilityRef = ReadScalar(Assert.IsType<YamlMappingNode>(ReadNode(compatibilityCheckout, "with")), "ref");

        var source = File.ReadAllText(FindRepositoryFile(".github", "workflows", "publish-image.yml"));
        var publication = new YamlStream();
        publication.Load(new StringReader(source));
        var publicationRoot = Assert.IsType<YamlMappingNode>(Assert.Single(publication.Documents).RootNode);
        var publicationJobs = Assert.IsType<YamlMappingNode>(ReadNode(publicationRoot, "jobs"));
        var publish = Assert.IsType<YamlMappingNode>(ReadNode(publicationJobs, "publish"));
        var inputs = Assert.IsType<YamlMappingNode>(ReadNode(publish, "with"));

        Assert.Equal(
            "MALIEV-Co-Ltd/Legacy.Maliev.Workflows/.github/workflows/publish-image.yml@503e8846390a597c267d2889b33a9c26863389b3",
            ReadScalar(publish, "uses"));

        Assert.Matches("^[0-9a-f]{40}$", serviceDefaultsRef);
        Assert.Matches("^[0-9a-f]{40}$", compatibilityRef);
        Assert.Equal(serviceDefaultsRef, ReadScalar(inputs, "legacy-service-defaults-ref"));
        Assert.Equal(compatibilityRef, ReadScalar(inputs, "compatibility-contracts-ref"));
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
            "MALIEV-Co-Ltd/Legacy.Maliev.Workflows/actions/dotnet-validate@e3a6093324a24968876782153286f52db8b29fd8",
            "MALIEV-Co-Ltd/Legacy.Maliev.Workflows/actions/dotnet-validate@main # e3a6093324a24968876782153286f52db8b29fd8");
    }

    [Fact]
    public void BuildAndTest_RejectsCommentedDependencySha()
    {
        AssertMutationRejected(
            "ref: 8f4f5f27b226ffe406c4c79b1903742e8c2e7dd3",
            "ref: main # 8f4f5f27b226ffe406c4c79b1903742e8c2e7dd3");
    }

    [Theory]
    [InlineData("ref: a5ac83a99130e273e4dae29b0c4bab87e2feca56", "ref: main")]
    [InlineData("repository: MALIEV-Co-Ltd/Legacy.Maliev.Workflows", "repository: MALIEV-Co-Ltd/Legacy.Maliev.FileService")]
    [InlineData("path: .dependencies/Legacy.Maliev.Workflows", "path: .dependencies/unapproved")]
    public void BuildAndTest_RejectsChangedOfflineImageProducer(string original, string replacement)
    {
        AssertMutationRejected(original, replacement);
    }

    [Fact]
    public void ApiProject_UsesOnlyLegacyServiceDefaults()
    {
        Assert.Contains("Legacy.Maliev.ServiceDefaults", ApiProject, StringComparison.Ordinal);
        Assert.DoesNotContain("Maliev.Aspire\\Maliev.Aspire.ServiceDefaults", ApiProject, StringComparison.Ordinal);
        Assert.DoesNotContain("Include=\"Maliev.Aspire.ServiceDefaults\"", ApiProject, StringComparison.Ordinal);
    }

    [Fact]
    public void EfDesignDependency_IsOwnedByDataProjectOnly()
    {
        Assert.DoesNotContain("Microsoft.EntityFrameworkCore.Design", ApiProject, StringComparison.Ordinal);
        Assert.Contains("Microsoft.EntityFrameworkCore.Design", DataProject, StringComparison.Ordinal);
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
    private const string SharedValidationAction = "MALIEV-Co-Ltd/Legacy.Maliev.Workflows/actions/dotnet-validate@e3a6093324a24968876782153286f52db8b29fd8";

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
        if (steps.Children.Count != 9)
        {
            throw new InvalidOperationException("Validate job must contain five pinned checkout steps, precompile consumer hash, validation, and two evidence steps.");
        }

        var environment = RequireMapping(validateJob, "env");
        if (environment.Children.Count != 4)
        {
            throw new InvalidOperationException("Validate environment must contain only dependency root and evidence properties.");
        }

        RequireScalarValue(environment, "MalievWorkspaceRoot", "${{ github.workspace }}/.dependencies");
        RequireScalarValue(environment, "VSTestCollect", "XPlat Code Coverage");
        RequireScalarValue(environment, "VSTestLogger", "trx");
        RequireScalarValue(environment, "VSTestResultsDirectory", "${{ github.workspace }}/runner-results");

        var gate = RequireMapping(steps.Children[7], "coverage gate");
        if (gate.Children.Count != 2)
        {
            throw new InvalidOperationException("Coverage gate must contain only name and run.");
        }

        RequireScalarValue(gate, "name", "Gate owned production coverage");
        RequireScalarValue(gate, "run", "python3 scripts/verify-runner-coverage.py runner-results");
        var evidence = RequireMapping(steps.Children[8], "evidence upload");
        if (evidence.Children.Count != 4)
        {
            throw new InvalidOperationException("Evidence upload must contain exactly name, if, uses and with.");
        }

        RequireScalarValue(evidence, "name", "Preserve validation evidence");
        RequireScalarValue(evidence, "if", "always()");
        RequireScalarValue(evidence, "uses", "actions/upload-artifact@ea165f8d65b6e75b540449e92b4886f43607fa02");
        var evidenceInputs = RequireMapping(evidence, "with");
        if (evidenceInputs.Children.Count != 4)
        {
            throw new InvalidOperationException("Evidence upload must have exactly four bounded inputs.");
        }

        RequireScalarValue(evidenceInputs, "name", "file-validation-${{ github.sha }}");
        RequireScalarValue(evidenceInputs, "path", "runner-results");
        RequireScalarValue(evidenceInputs, "if-no-files-found", "warn");
        RequireScalarValue(evidenceInputs, "retention-days", "7");

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
                ["ref"] = "8f4f5f27b226ffe406c4c79b1903742e8c2e7dd3",
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
            CheckoutAction,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["repository"] = "MALIEV-Co-Ltd/Legacy.Maliev.Workflows",
                ["ref"] = "a5ac83a99130e273e4dae29b0c4bab87e2feca56",
                ["path"] = ".dependencies/Legacy.Maliev.Workflows",
                ["persist-credentials"] = "false",
            });
        ValidateStep(
            steps.Children[4],
            CheckoutAction,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["repository"] = "MALIEV-Co-Ltd/Legacy.Maliev.Intranet",
                ["ref"] = "3f5f7542c93cb085757130971c4fc7cf61043f01",
                ["path"] = ".dependencies/Legacy.Maliev.Intranet",
                ["persist-credentials"] = "false",
            });
        var consumerHash = RequireMapping(steps.Children[5], "consumer hash");
        if (consumerHash.Children.Count != 2) throw new InvalidOperationException("Consumer hash must contain only name and run.");
        RequireScalarValue(consumerHash, "name", "Verify exact consumer source before compilation");
        RequireScalarValue(consumerHash, "run", "git -C .dependencies/Legacy.Maliev.Intranet show 3f5f7542c93cb085757130971c4fc7cf61043f01:Legacy.Maliev.Intranet/PurchaseOrders/LegacyFileClient.cs | sha256sum | awk '$1 != \"98a4b2954e9dede6c3d237af1ae9b42198dec7f61835af22a61e601ded731bea\" { exit 1 }'\n"
            + "echo 'a4a31a998cc168709d0914fa19b4836f5da219c73a3d6f99c7a2e40d15429758  .dependencies/Legacy.Maliev.Intranet/Legacy.Maliev.Intranet/PurchaseOrders/LegacyFileClient.cs' | sha256sum --check --strict\n");
        ValidateStep(
            steps.Children[6],
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
