# Google SDK 1.77 acceptance, 2026-10-01

Bounded issue46, superseding Dependabot PR43 on current File protected main
`7e24d9e8ec17e03e75b3ea07924facb645eabe09` (recovery PR42). The bot patch changes
only Google.Apis.Auth1.76 to1.77; this acceptance adds executable SDK regressions.
No application/storage/journal behavior or credential policy is changed.

Official upstream tag `v1.77.0`, commit
`a7d25ddf34d6e8db8a106e93dfd7baf0347d4f61`, release/PR3236 hardens path
parameters against traversal and parameter injection:
https://github.com/googleapis/google-api-dotnet-client/releases/tag/v1.77.0
Tagged RequestBuilder source was independently read through GitHub API. Context7
is unavailable; official tagged source and installed XML documentation were used.

## Executed regression evidence

Real installed generated StorageService DELETE request construction verifies
Thai/slashes/reserved/query/percent characters remain one encoded object value,
with exact Generation17 and IfGenerationMatch17 independently present. No HTTP
request is executed. Real UrlSigner uses an ephemeral in-memory RSA2048
ServiceAccountCredential, never ADC/cloud/key files; adapter produces V4 URLs,
bounded one-hour/seven-day expiry and correctly decoded Thai attachment filename.
Actual RequestBuilder plus generated object requests reject dot segments.

Initial compile missed RequestParameterType's Util namespace; corrected before
execution, not product RED. Initial signing assertions incorrectly compared raw
RFC5987 header text to decoded Thai; corrected to ContentDispositionHeaderValue
FileNameStar, not a runtime defect. Corrected baseline1.76 focus: four genuine
dot-segment RED, six controls passing, zero skipped. TRX:
`root-auth177-red-corrected/natth_MALIEV-31USFIV_2026-10-01_11_59_26_net10.0.trx`.
After the one-line dependency update, Release0warnings/0errors, focus10/full567
passed, zero skips. TRXs `root-auth177-green/...11_59_52_net10.0.trx` and
`root-auth177-full/...11_59_54_net10.0.trx`.
After scoped formatting and joined compilation, fresh pinned Release again
reports zero warnings/errors, focus10/full567 pass with zero skips; final TRXs
`root-auth177-final-focus/...12_04_23_net10.0.trx` and
`root-auth177-final-full/...12_04_24_net10.0.trx`. Test/document gitleaks scans
report no leaks. Earlier baseline/final artifacts are ignored, not staged.

Private exact CI pins Defaults8f4f5f27b226ffe406c4c79b1903742e8c2e7dd3 and
Contracts78e48ffc4ee000df0510cba5e7c7a3c4c4d539d7. Builds/tests/static commands use
`UseLocalMalievDependencies=true` and this worktree's ignored
`TestResults/.private` workspace. Build first is the actual Tests csproj Release
with TreatWarningsAsErrors=true; focus filter GoogleSdk177CompatibilityTests;
fullsuite includes every existing557 test unchanged. Whole solution format and
all five transitive vulnerability audits pass. No exclusions/threshold changes.

Actual assets resolve Google.Apis/Auth/Core1.77.0, Storage.V15.0.0,
generated Storage1.76.0.4250 and Gax4.13.1. RequestBuilder hardening comes from the
resolved Core, not a claim that Auth credential logic itself changed.

## Joined compilation

Independent committed-main private clones, no canonical output writes:
AppHost55d80acfeebf58d17a07f8d3226cc82a8dd38d79,
Auth8cdb634b3b0abdf18b9b826a0948dbfd98c66ea0,
Defaults515c5898a478eef914d5515eabd95930c1d2d8b7,
Contracts47f8f94df02b6d5bb6dfda614ea179fea5d52588,
plus current Country/Customer/Employee/Catalog/Procurement/Order/Quotation/
Career/Contact/Accounting main clones. File candidate is the only changed
dependency. Actual AppHost MigrationRunner Release build reports zero warnings
and errors; no Google downgrade or assembly mismatch. Console is NOT executed.
This is joined compilation, not migration/provider/Aspire runtime acceptance.

## Boundaries

No GCS/network credential request, live WIF/IAM signing grant, ClamAV activation,
source mutation, persistent DDL/data write, deployment or traffic change.
File41/13/provider/schema and quality45 remain open. Bot43 can be closed as
superseded only after this protected-main PR and exact-main CI are accepted.
This dependency update does not resolve an original migration ledger owner.
