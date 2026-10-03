# Source identity and publication obligations

This note records source review for [issue 50](https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.FileService/issues/50). It changes no runtime, workflow, deployment, permission, or migration disposition. Publisher tests do not establish runtime workload identity.

## Reviewed source

The reviewed private source head is `135e526d0dab85c415b3afdcefd7b70fe2c82e2f`. Source objects were read without changing the original repository. The obligations below originate in `c3450c9d9a75f04b32eeed91d05d95b9f7a2c449`, parent `ca2182c077bd4dde532a99a00b5cd945c1ed1849`, which added the upload workload-identity deployment tests.

Two earlier credential-wrapper paths are absent at that head: `Maliev.UploadService.GoogleCloudStorage/IServiceAccount.cs` and `ServiceAccount.cs`. Both were removed by `56014f2efc7c7f24b777c51d17a454e023d12bdd` when credential acquisition moved to an injected `IGoogleCredentialProvider`. The retained provider supports Application Default Credentials. This is source supersession evidence, not authorization to restore embedded credentials or a claim that runtime identity is configured. This compatibility service requires ADC/WIF and must not add credential files.

The generated `Maliev.UploadService.Api.xml` path was removed by `03dc9a1271c16e6535934445e9dd6e3f30e8fffe` to isolate generated XML documentation from source. Its removal does not retire the corresponding public API documentation behavior. Source-path absence alone does not close an obligation.

## Boundaries that remain open

| Original guard | What publisher configuration can establish | Required additional evidence |
| --- | --- | --- |
| Dedicated runtime Kubernetes service account | Dedicated publisher identity input only | Runtime Deployment service-account binding |
| Dedicated Google identity and projected token | Publisher WIF inputs only | Runtime KSA/GSA mapping and token availability |
| Service account applied before Deployment | No runtime apply is performed by these tests | Reviewed infrastructure application ordering |
| Application-only release changes image only | Dormant publisher admission and legacy image/context scope | Existing-workload image update without infrastructure mutation |
| Narrow object, signing, and WIF roles | Minimal GitHub publisher permissions | Separately reviewed runtime IAM/bucket/signing roles |
| IAM preflight before image construction | Immutable reusable-workflow pin | Actual shared producer preflight/build ordering |

All six obligations remain pending until the responsible producer/operations boundaries have matching evidence. No live IAM, deployment, storage, or customer-data acceptance is asserted here.

Code migration can be accepted through real credential-adapter tests and reviewed rendered-manifest, preflight, and ordering fixtures without applying live infrastructure. The pending status here reflects missing code-boundary evidence. Actual runtime KSA/GSA/IAM deployment and cloud access remain separately deferred operational readiness work; they are not prerequisites for completing the source-code migration.

## Local publisher checks

The repository's [publisher workflow](../.github/workflows/publish-image.yml) is dormant unless its explicit legacy deployment gate is enabled. [Publisher contract tests](../Legacy.Maliev.FileService.Tests/PublishWorkflowPermissionContractTests.cs) cover the reviewed reusable producer pin, exact dependency pins, minimal job permissions, dedicated publisher inputs, and complementary admission conditions. Draft negative cases reject widened image/context/environment scope, floating dependencies, missing identity inputs, and weakened conditions.

Those draft cases require their own strict build and focused/full test validation before acceptance. Documentation readback, link checks, and secret scanning of this note do not validate those tests or substitute for service coverage, required CI, or deployment readiness.
