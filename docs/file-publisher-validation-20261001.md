# File dormant publisher validation adoption

## Scope and compatibility

Base `71adaa55be48dd7be655da0f12643ae1b1aa41e1` was clean and matched live origin/main; no open File PR at preflight. Owned branch/worktree: `codex/file-publisher-validation-20261001`, `B:/maliev-legacy/.worktrees/file-publisher-validation-20261001`. All existing worktrees were preserved.

Only the reusable publisher pin changes from `73dd7304ffe85ec504389fd7664cc39070b9f148` to reviewed `503e8846390a597c267d2889b33a9c26863389b3`, with job-local `actions: read`. Exact publisher permissions are contents/read, actions/read and id-token/write. Workflow-level contents/read and both complementary `LEGACY_DEPLOY_ENABLED` conditions remain unchanged. No OIDC authority is added to the planned-only job.

The entire committed `on.workflow_call.inputs` section at File's actual old pin and new pin was compared ordinally: identical ten definitions, including descriptions, required flags, string types and defaults. Six required publication inputs remain required; optional maliev-aspire-ref, messaging-contracts-ref, legacy-service-defaults-ref and compatibility-contracts-ref retain empty-string defaults. File's existing eight explicit inputs are unchanged, including ServiceDefaults `8f4f5f27b226ffe406c4c79b1903742e8c2e7dd3` and CompatibilityContracts `78e48ffc4ee000df0510cba5e7c7a3c4c4d539d7`. Private clean detached clones use those exact refs. Build validation still uses its original `73dd7304ffe85ec504389fd7664cc39070b9f148` action.

SDK 1.77 fixes, Google transport, Dockerfile/context, private refs, runtime, configuration, flags and API contracts are untouched. The existing publisher dependency test changes only its obsolete exact publisher-pin expectation; all dependency equality and other controls remain. New parsed tests enforce minimal permissions, gates and all eight inputs, with seven permission mutants checked against a validated positive baseline.

## Individual source cohort

Source was inspected only from committed objects in `B:/maliev-legacy/.artifacts/source-commit-mirror-20260930.git`, cutoff `bed10c7d15e0698e0b75f1329d0f312937f5d77f`. No original edits/fetches.

| Full source SHA | File-owned source paths | Source behavior and bounded disposition |
| --- | --- | --- |
| `00ec830615c15b5e4e227046712247b11df0100f` | `Maliev.UploadService.Api/deploy.ps1` | Native exit checks, explicit failure propagation, finally cleanup, GCP preflight. Reviewed reusable publication adopts fail-closed immutable publication, not imperative GCP/kubectl execution. |
| `72163e9ae11f39f6579423841a2e20529b986fab` | `Maliev.UploadService.Api/deploy.ps1`, `Maliev.UploadService.Api/deploy-service.ps1` | Explicit deployment-success state and wrapper kubectl exit propagation rather than ambient error/stderr inference. Publisher adoption covers publication outcome only, not service apply. |
| `f8921b1b1d5846eeaff999af10b640011655d1d4` | `Maliev.UploadService.Api/deploy.ps1` | Version rendering into throwaway manifest, missing-placeholder refusal and finally cleanup without template mutation. No mutable manifest port; downstream GitOps rollout remains separately gated. |

No shared-cohort retirement, source-owner closure or quality waiver is implied. The new reusable producer checks exact caller main SHA/protected CI before immutable publication. This local slice does not execute that GitHub publisher, registry/WIF or deployment. Migration/parity/rollback/capacity/secrets/GitOps/staging activation gates remain unchanged.

## Executed evidence

All commands ran sequentially in the owned worktree, with `UseLocalMalievDependencies=true` and `MalievWorkspaceRoot=<owned-worktree>/.dependencies` for dotnet commands. No overlapping build/test/format outputs.

- Baseline `dotnet build Legacy.Maliev.FileService.slnx -c Release -p:UseLocalMalievDependencies=true -p:MalievWorkspaceRoot=$env:MalievWorkspaceRoot -nodeReuse:false`: zero warnings/errors. Original full suite: 567 passed, zero failed/skipped; `TestResults/file-publisher-baseline/natth_MALIEV-31USFIV_2026-10-01_14_24_08_net10.0.trx`.
- New test-first Release: zero warnings/errors. Focused RED: 23 passed, 2 intended failures (old publisher pin and missing actions/read), zero skips; `TestResults/file-publisher-red/natth_MALIEV-31USFIV_2026-10-01_14_25_19_net10.0.trx`.
- Scoped formatting then final Release build: zero warnings/errors.
- `dotnet test Legacy.Maliev.FileService.slnx -c Release --no-build --no-restore --filter 'FullyQualifiedName~PublishWorkflowPermissionContractTests|FullyQualifiedName~WorkflowContractTests' --logger trx --results-directory TestResults/file-publisher-focus`: 25 passed, zero failed/skipped; `natth_MALIEV-31USFIV_2026-10-01_14_26_02_net10.0.trx`.
- `dotnet test Legacy.Maliev.FileService.slnx -c Release --no-build --no-restore --logger trx --results-directory TestResults/file-publisher-full`: 576 passed, zero failed/skipped; `natth_MALIEV-31USFIV_2026-10-01_14_26_04_net10.0.trx`.
- Whole solution `dotnet format Legacy.Maliev.FileService.slnx --verify-no-changes --no-restore`, whole-repository `actionlint` v1.7.12 and `git diff --check`: passed.
- `dotnet list Legacy.Maliev.FileService.slnx package --vulnerable --include-transitive --no-restore`: no vulnerable packages in all five projects against current NuGet sources.
- `gitleaks git . --redact --no-banner`: 83 committed revisions, no leaks. Final four-owned-file `gitleaks stdin --redact --no-banner` and the exact CI-pinned `73dd7304ffe85ec504389fd7664cc39070b9f148:scripts/JwtSigningResourceScanner.ps1` scanner: no findings.

Candidate is frozen for root independent review. No commit/push, production provider access, deployment or persistent data changes were performed. Local workflow contracts do not claim remote publisher or deployment acceptance.

## Independent root acceptance — bounded issue #48

Root independently built the direct test project with all private dependencies in Release and warnings as errors: zero warnings/errors. Focused 25 and unfiltered 576 tests passed with zero skips (`TestResults/root-publisher-focus/root-focus.trx`, `TestResults/root-publisher-full/root-full.trx`). Whole-solution formatting, actionlint, whitespace checks and all five transitive vulnerability audits passed. An unsuppressed scoped stdin secret scan covered 33,250 bytes with zero findings before this acceptance note. Protected-head and post-merge exact-main CI remain required before closing the bounded issue. No broader source-owner, provider or Aspire acceptance is implied.
