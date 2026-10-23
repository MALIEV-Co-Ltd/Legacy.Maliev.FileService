# Source identity and publication obligations

This note records source review for [issue 50](https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.FileService/issues/50). It changes no runtime, workflow, deployment, permission, or migration disposition. Publisher tests do not establish runtime workload identity.

## Reviewed source

The reviewed private source head is `135e526d0dab85c415b3afdcefd7b70fe2c82e2f`. Source objects were read without changing the original repository. The upload workload-identity suite at `Maliev.Web.Tests/UploadServiceWorkloadIdentityDeploymentTests.cs` developed through these source changes:

- `4533669fa5231368f17c4b59b17c3e2f52e24a89`, parent `6de82fd9760e86c71ddba3085879a63b43faff9f`, introduced three guards: dedicated Kubernetes service account, dedicated Google service account mapping, and service-account-before-Deployment ordering. Its file blob is `a0d907e96ec301cebca2825139745b22aad8af87`.
- `25418c95b5ac79400029ce274541f0e51728da3e`, parent `4533669fa5231368f17c4b59b17c3e2f52e24a89`, added narrow IAM roles and IAM preflight before image construction.
- `1ec9ef70db2010936964b51a741bf282133298fa`, parent `f14d3ef9c294783a34d1196050f51f682aab8316`, added the application-only infrastructure guard and namespace checks, bringing the suite to six guards.
- `c3450c9d9a75f04b32eeed91d05d95b9f7a2c449`, parent `ca2182c077bd4dde532a99a00b5cd945c1ed1849`, modified the existing application-only case; its file blob is `ec4ce2f3392b7f13ee1f9c309589e88d2c8eec62`. The exceptional safe-rollout patch assertions apply only to CustomerService with 90-second readiness and 60-second drain settings. FileService retains the prohibition on workload-identity infrastructure mutation. Customer-specific rollout equivalence belongs to Customer issue 40 and the shared Workflows owner.

Two earlier credential-wrapper paths are absent at that head: `Maliev.UploadService.GoogleCloudStorage/IServiceAccount.cs` and `ServiceAccount.cs`. Both were removed by `56014f2efc7c7f24b777c51d17a454e023d12bdd` when credential acquisition moved to an injected `IGoogleCredentialProvider`. The retained provider supports Application Default Credentials. This is source supersession evidence, not authorization to restore embedded credentials or a claim that runtime identity is configured. This compatibility service requires ADC/WIF and must not add credential files.

The generated `Maliev.UploadService.Api.xml` path was removed by `03dc9a1271c16e6535934445e9dd6e3f30e8fffe` to isolate generated XML documentation from source. Its removal does not retire the corresponding public API documentation behavior. Source-path absence alone does not close an obligation.

## Offline code-equivalence evidence

| Original guard | What publisher configuration can establish | Required code-equivalence evidence without applying resources |
| --- | --- | --- |
| Dedicated runtime Kubernetes service account | Dedicated publisher identity input only | Real emitted Deployment selects the dedicated KSA |
| Dedicated Google identity and projected token | Publisher WIF inputs only | Parsed KSA annotation/token intent, namespace/KSA/GSA mismatch controls, and actual ADC registration |
| Service account applied before Deployment | No runtime apply is performed by these tests | Actual plan through a recording adapter proves KSA-before-Deployment ordering and failure stops subsequent calls |
| Application-only release changes image only | Dormant publisher admission and legacy image/context scope | Actual named-image update adapter preserves KSA, environment, sidecars, and infrastructure |
| Narrow object, signing, and WIF roles | Minimal GitHub publisher permissions | Real policy parser/admission checks literal bucket `storage.objectUser`, KSA `workloadIdentityUser`, and signing `serviceAccountTokenCreator` roles |
| IAM preflight before image construction | Immutable reusable-workflow pin | Actual orchestrator with controlled policy reader/image builder proves IAM-read failure makes zero build, publish, or workload calls |

All six FileService-owned offline code predicates were independently reviewed at protected main `752994cb0a99393bdc8d0979b02cd21aa25c803b`. The evidence below accepts code equivalence only. No live IAM, deployment, storage, or customer-data acceptance is asserted here.

Code migration can be accepted through real credential-adapter tests and reviewed rendered-manifest, preflight, and ordering fixtures without applying live infrastructure. Actual runtime KSA/GSA/IAM deployment and cloud access remain separately deferred operational readiness work; they are not prerequisites for completing the source-code migration.

The actual target evidence is:

- `scripts/Render-DormantLegacyFileDeployment.ps1` and dormant manifest tests verify the emitted dedicated KSA selection. `scripts/Get-LegacyFileIdentityPlan.ps1` parses namespace, KSA/GSA annotation and projected-token intent with mismatch rejection; runtime registration retains `StorageClient.Create()` default ADC acquisition. Credential tests use a synthetic injected credential and prove adapter/signing registration, not ambient metadata discovery or effective cluster identity.
- `scripts/Invoke-LegacyFileIdentityResourceApplication.ps1` passes the real ordered resource plan to a recording adapter, requires exact acknowledgements and stops after failure. It applies no live resources.
- The identity planner invokes the pinned image-only producer. Controlled fixtures compare the whole emitted document and unchanged input, preserving nonselected images, KSA, environment and infrastructure.
- The literal IAM parser validates bucket `storage.objectUser`, KSA `workloadIdentityUser` and signing `serviceAccountTokenCreator` roles and rejects mismatches. The orchestrator admits this policy before image construction; the failing-reader fixture records no image-build call.

[Exact-main hosted validation](https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.FileService/actions/runs/39910904229) passed a Release build with zero warnings/errors and 1,717 full-suite cases. Actual passed groups include 83 identity-plan cases, 28 recording-resource cases, 17 dormant-manifest cases, six credential-registration cases and 22 publisher cases. These are constituent groups of the full suite, not additional tests. Raw generated-inclusive coverage exceeded the unchanged 80% floor for each required assembly. Shared producer issue 265 and File consumer PR 57 supply producer provenance; the target evidence above establishes the six File-owned predicates. Customer issue 40 and shared Workflows obligations retain Customer-only rollout behavior.

## Local publisher checks

The repository's [publisher workflow](../.github/workflows/publish-image.yml) is dormant unless its explicit legacy deployment gate is enabled. [Publisher contract tests](../Legacy.Maliev.FileService.Tests/PublishWorkflowPermissionContractTests.cs) cover the reviewed reusable producer pin, exact dependency pins, minimal job permissions, dedicated publisher inputs, and complementary admission conditions. Negative cases reject widened image/context/environment scope, floating dependencies, missing identity inputs, and weakened conditions.

These publisher cases passed in the exact-main run cited above. Documentation readback, link checks, and secret scanning alone do not validate runtime tests or establish deployment readiness.
