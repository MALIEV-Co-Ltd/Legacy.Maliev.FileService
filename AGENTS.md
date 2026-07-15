# Legacy.Maliev.FileService

This public repository is the sanitized .NET 10 compatibility extraction of
`Maliev.UploadService` from the private `R:\maliev-web` monorepo.

## Non-negotiable boundaries

- Keep the original `maliev-web` repository private and do not copy its Git history.
- Never add service-account JSON, private keys, JWT material, connection strings,
  legacy `Resources` credentials, or generated secret-audit evidence.
- Use Google Application Default Credentials and GKE Workload Identity only.
- Preserve `/Uploads`, `/uploads/SignedUrl`, multipart field `files`, legacy query
  names, PascalCase response JSON, and the 200 MB aggregate upload limit.
- Every upload must remain private until a complete-file malware scan returns clean.
  Scanner unavailable, timeout, error, or unknown results fail closed.
- Signed URLs require clean `Upload` metadata and expire within seven days.
- Preserve the legacy `Upload` table/column mappings. Do not mutate source SQL Server.
- Do not deploy over the new `Maliev.FileService` identity or GitOps path.

## Service conventions

- Runtime: .NET 10.
- API documentation: Scalar/OpenAPI through `Maliev.Aspire.ServiceDefaults`; no Swagger.
- Logging: built-in `ILogger<T>` at warning/error for actionable failures only.
- Auth: granular `legacy-file.uploads.*` permissions on every business endpoint.
- Storage: private GCS objects using ADC/WIF; no public ACLs or credential files.
- Malware scanning: ClamAV INSTREAM with quarantine-to-clean promotion.
- Data: PostgreSQL target only after parity gates; original database remains source of truth.
- Cache: do not cache signed URLs or clean-object authorization because delete/move
  must revoke immediately.

## Required validation

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet format Legacy.Maliev.FileService.slnx --verify-no-changes --no-restore
dotnet list package --vulnerable --include-transitive
gitleaks git . --redact=100 --exit-code 0 --no-banner --no-color
```

Contract or storage-boundary changes require matching tests for route/wire shape,
quarantine behavior, fail-closed scanning, object-path safety, clean metadata checks,
and a real PostgreSQL migration.
