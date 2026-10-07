# Bounded credential retirement: source 56014f2

Source `56014f2efc7c7f24b777c51d17a454e023d12bdd`, parent `b6c8c6a18f2a782b59f988c24f5e48a30c51cfe0`, has thirteen FileService path memberships. This addendum records four credential-related portions of the remaining ten. The original [historical map](source-credential-adaptation-56014.json) is unchanged; its pending fields remain historical evidence, not a current acceptance claim.

The repository requires Google Application Default Credentials and GKE Workload Identity, and forbids credential files and legacy resource credentials. Under that policy:

| Source path | Bounded disposition |
| --- | --- |
| `Maliev.UploadService.GoogleCloudStorage/IServiceAccount.cs` | Retire the credential-wrapper portion; current ADC registration owns credential acquisition. No original public CLR interface compatibility is claimed. |
| `Maliev.UploadService.GoogleCloudStorage/ServiceAccount.cs` | Retire the legacy credential-wrapper portion under ADC-only policy. No credential-file fallback or original wrapper consumer compatibility is claimed. |
| `Maliev.UploadService.GoogleCloudStorage/Properties/Resources.Designer.cs` | Retire the generated credential-resource accessor portion. Other source behavior is not accepted by this entry. |
| `Maliev.UploadService.GoogleCloudStorage/Properties/Resources.resx` | Retire the credential-resource portion; no resource values are copied or activated. Other resource semantics are not accepted by this entry. |

Six obligations remain separate:

| Source path | Remaining obligation |
| --- | --- |
| `Maliev.UploadService.Api/Maliev.UploadService.Api.xml` | Associate generated API XML with current API documentation and underlying controller contracts; documentation alone does not close executable behavior. |
| `Maliev.UploadService.Api/ServiceExtensions.cs` | Review the complete registration and option behavior, including the deliberate form-admission adaptation. Credential retirement does not accept this whole file. |
| `Maliev.UploadService.Api/Startup.cs` | Review complete host, middleware, configuration and authentication behavior against current Program/registration. |
| `Maliev.UploadService.GoogleCloudStorage/GoogleCloudStorage.cs` | Review complete storage behavior and actual caller contracts; retiring credentials does not establish provider parity. |
| `Maliev.UploadService.GoogleCloudStorage/Maliev.UploadService.GoogleCloudStorage.xml` | Associate generated Data XML with the current storage implementation and its underlying tests. |
| `Maliev.UploadService.Tests/Maliev.UploadService.Tests.xml` | Use actual test execution/TRX as the substitute for historical generated test documentation. The current test project does **not** enable GenerateDocumentationFile; regenerated test XML is not claimed. |

API and Data projects enable `GenerateDocumentationFile`; the test project does not. These are inspected source flags, not evidence of a new compiler execution for this docs-only change.

Accepted baseline is main `339b2d59ed54a66d07bf5e4007da292713615ccd`, with [fresh run 37565159189](https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.FileService/actions/runs/37565159189) and 1,021 passing cases. That suite does not automatically accept every source path. Current ADC/IAM provider credentials, credential-file fallback, original public CLR API compatibility and complete source-SHA closure are excluded. The preceding three-path signing proposal remains separate.

Immutable owner-review evidence: remaining-ten packet SHA-256 `c2b74027ffc172b1e0bdde515de4965452133a9fb6c236d966b6d726b9a06c65`; accepted baseline readback SHA-256 `678c2dd44a50cd5668c6c70887d65601f36a170a7c767e2f335fc0000adc209b`; committed LF historical-map blob SHA-256 `25ff82af7ec533a191a7624ea89a2aa9ca78b8222b7a71235ef8f56aa4a548f9`. These identify reviewed evidence; no private credential/resource bodies or secret-audit reports are included.

This addendum changes documentation only. It does not change production configuration, storage, authentication, tests, deployment, source history or the canonical migration registry. Grouped tracking: [Workflows #98](https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.Workflows/issues/98).

The owner worktree CRLF projection of the unchanged historical map has SHA-256 `52d059de32ca7b4b27737cd63811bd7edc2a03666c33fd5ce71a12de7f25c95b` (13,567 bytes, 334 CRLFs). The baseline and this PR retain the identical committed LF blob above (13,233 bytes); normalizing only CRLF to LF gives that same hash. No historical-map content was edited.
