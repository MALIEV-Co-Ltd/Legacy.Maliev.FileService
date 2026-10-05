# Dormant File deployment artifact

This Deployment template is excluded from active GitOps. It has zero replicas,
storage and Instant Quote mutations disabled, and no image default. The renderer
requires an explicitly approved repository in the existing File identity-plan
scope (Artifact Registry, project `maliev-website`, image `legacy-maliev-file-service`)
and an exactly matching nonzero lowercase SHA256 digest. Neither input has a default.
Rendering emits YAML to stdout; it does not build, publish, apply,
create infrastructure, enable a feature, or verify that an image exists.

The target identity is `maliev-legacy/legacy-maliev-file`; the pod references the
existing `legacy-maliev-file` service account and the required managed secret
`legacy-maliev-file-runtime`. No Secret, service account, namespace, SQL connection
value, or credential is created here. These references were inspected at GitOps
`9598923957dad5e962559e38cf1167bed8cd3fbc`: service-account blob
`e41db21765f1e5c3f1780842f6abdef320708406`, ExternalSecret blob
`6e775e6722221866a0414a9f9af8af6fb7d8f8e3`.

The existing account annotates the dedicated File Google identity and disables
service-account token automount. The pod preserves that setting. The accepted
offline `Get-LegacyFileIdentityPlan.ps1` guard requires token automount enabled.
This mismatch remains blocked; the template does not satisfy that guard, change
the account, override it, relax the guard, or establish live Workload Identity.

The original source cohort is `4533669fa5231368f17c4b59b17c3e2f52e24a89`, parent
`6de82fd9760e86c71ddba3085879a63b43faff9f`, covering
`Maliev.UploadService.Api/deploy.ps1`, `deployment.yaml`, `service-account.yaml`,
and `Maliev.Web.Tests/UploadServiceWorkloadIdentityDeploymentTests.cs`. This
artifact addresses a missing Deployment that references the retained identity;
it does not close all four paths or the original apply/preflight/image-only
release behavior. Existing image-plan and publication gates stay unchanged.

Port 8080 follows the current File Docker runtime. `/file/liveness` and
`/file/readiness` follow Program's `MapDefaultEndpoints("file")` registration.
The original File resource requests (50m/256Mi) and limits (200m/512Mi) are retained
as a dormant starting point, with no load-capacity claim. The hardened read-only
container gets a bounded disk-backed `/tmp` for ASP.NET temporary files, with pod
`fsGroup: 1654` and `fsGroupChangePolicy: OnRootMismatch`. These manifest fields
express the non-root volume policy; runtime writability has not been tested.

Activation still requires owner-approved Aspire and deployment gates, the actual
validated image digest and approved repository, existing managed-secret readiness, explicit resolution
of token intent and IAM preflight, and separate desired-state review. No active
GitOps inclusion or deployment is authorized by this artifact.

At authoring, `LEGACY_DEPLOY_ENABLED` is false and `LEGACY_ARTIFACT_REGISTRY` is
not configured in the File repository. No repository or published digest is
inferred from another service. The test repository and digest are synthetic inputs.
