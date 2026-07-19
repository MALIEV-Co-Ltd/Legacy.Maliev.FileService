# Legacy.Maliev.FileService

[![PR validation](https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.FileService/actions/workflows/pr-validation.yml/badge.svg)](https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.FileService/actions/workflows/pr-validation.yml)
[![Main CI](https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.FileService/actions/workflows/ci-main.yml/badge.svg)](https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.FileService/actions/workflows/ci-main.yml)

Public, sanitized .NET 10 compatibility service extracted from the old
`Maliev.UploadService` in the private `maliev-web` monorepo. It preserves the
legacy file HTTP and JSON contracts while replacing embedded Google credentials,
direct-to-final uploads, and unverified signed URLs with explicit security boundaries.

## Architecture

Dependency direction is `Api -> Application -> Domain`; PostgreSQL, ClamAV, and
Google Cloud Storage adapters live in `Data`. The service uses Scalar/OpenAPI through
the public `Legacy.Maliev.ServiceDefaults` package while preserving the existing
`Maliev.Aspire.ServiceDefaults` CLR namespace, built-in `ILogger<T>`, granular JWT permissions,
Application Default Credentials, and Workload Identity in GKE. CI and image builds also use the
public `Legacy.Maliev.CompatibilityContracts` source repository, so no new-platform shared-library
source or private package credentials are required.

The upload state transition is intentionally fail closed:

```text
multipart request -> private GCS quarantine -> complete-file ClamAV scan
                  -> clean-only promotion -> Upload metadata commit -> signed URL
```

If scanning reports malware, times out, is misconfigured, or returns an unknown
response, the object is not promoted and no clean metadata or signed URL is created.
The legacy service-account resource is not present in this repository.

## Preserved API contracts

| Purpose | Method | Route | Permission |
| --- | --- | --- | --- |
| Upload files | `POST` | `/Uploads?bucket=...&path=...` | `legacy-file.uploads.create` |
| Move an object | `PUT` | `/Uploads?sourceBucket=...` | `legacy-file.uploads.update` |
| Delete an object | `DELETE` | `/Uploads?bucket=...&objectName=...` | `legacy-file.uploads.delete` |
| Create read URL | `GET` | `/uploads/SignedUrl?bucket=...&objectName=...` | `legacy-file.uploads.read` |
| Scalar UI | `GET` | `/file/scalar` | anonymous documentation |

`POST /Uploads` keeps multipart field `files`, the optional custom `path`, the
200 MB aggregate limit, `201 Created` with location `Google Cloud Storage`, and
the PascalCase response shape:

```json
{
  "Object": [
    {
      "Bucket": "maliev.com",
      "ObjectName": "uploads/customer/file.stl",
      "Uri": "https://storage.googleapis.com/..."
    }
  ]
}
```

Callers that need replay safety send a stable `Idempotency-Key`. The service binds
that workflow key to the signed service principal and complete multipart payload,
then persists the exact `201` response for at least 24 hours. Changed payloads and
concurrent execution return `409`; ambiguous storage or metadata outcomes return
`503` and remain reconcilable without starting a second scanner execution.

Security corrections intentionally reject unapproved buckets and path traversal.
Signed URLs are capped at seven days, matching the effective maximum of the old
signer, and are issued only when clean `Upload` metadata exists.

## Runtime configuration

- PostgreSQL connection: `ConnectionStrings__FileDbContext`
- Existing shared Redis connection: `ConnectionStrings__redis`
- Allowed GCS buckets: `FileStorage__AllowedBuckets__*`
- Quarantine prefix: `FileStorage__QuarantinePrefix`
- Signed URL hours: `FileStorage__SignedUrlHours` (1-168)
- ClamAV service: `MalwareScanner__Host`, `MalwareScanner__Port`
- Namespace at deployment: `maliev-legacy`
- Planned database: `legacy-postgres-file`
- GCS authentication: Application Default Credentials / GKE Workload Identity only

GCS object privacy is enforced by private bucket IAM with Uniform Bucket-Level
Access (UBLA). The service does not set per-object ACLs; enabled instant-quotation
buckets must be distinct, lowercase DNS-compatible GCS names.

ClamAV must be configured for at least the legacy 200 MB request ceiling. Leaving
`MalwareScanner__Host` empty is safe for local startup, but all upload requests fail
closed with `503` until a scanner is available.

Redis is used only for fenced upload replay checkpoints. It is deliberately not
used for object authorization or newly generated signed URLs: caching either would
weaken delete/move revocation semantics. PostgreSQL clean metadata is the
authorization boundary; GCS handles object delivery directly.

## Migration and deployment boundary

The SQL Server source remains untouched. PostgreSQL migration creates the compatible
`Upload` table in a new target database, and historical rows must pass backup,
row-count/checksum, object-existence, signed-read, and rollback gates before cutover.

Deployment is validation-only until these dedicated resources exist:

- `legacy-maliev-file` GitHub/GCP Workload Identity binding;
- `maliev-gitops/3-apps/_legacy-file-service` manifests in `maliev-legacy`;
- a private GCS quarantine prefix and lifecycle policy;
- a resource-bounded ClamAV workload in the existing cluster;
- the `legacy-postgres-file` CloudNativePG target.

The new implementation's `maliev-file-service` identity and GitOps path must never
be reused or overwritten. No new node pool or paid Cloud SQL dependency is required.

## Validate

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet format Legacy.Maliev.FileService.slnx --verify-no-changes --no-restore
dotnet list package --vulnerable --include-transitive
gitleaks git . --redact=100 --exit-code 0 --no-banner --no-color
```
