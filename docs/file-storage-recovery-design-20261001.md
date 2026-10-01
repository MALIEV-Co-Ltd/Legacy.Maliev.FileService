# File storage recovery acceptance design — 2026-10-01

## Independent root code acceptance — bounded issue 44

Root reviewed the initial PR42 adapter/journal and the completed intent,
snapshot, metadata/compensation transaction, provenance, migration and fixture
changes together. Root rebuilt the exact private dependency graph in Release:
zero warnings/errors, affected focus 112 and unfiltered full 557 passed with
zero skips. Artifacts are `TestResults/root-recovery-focus` and
`TestResults/root-recovery-full`. Whole formatting, five dependency audits,
signing/current-tree scans and redacted test/document scans passed.
Unexcluded coverage: API 69.56%, Application 84.32%, Data 96.75%, Domain 95.48%.
Issue #45 retains the unmet API80 and broader route acceptance gate.

Issue #44 is only the reviewed code boundary; parent #41 and #13 remain open.
Protected-main CI and post-merge validation are required before code closure.
Direct GCS/WIF/ClamAV acceptance, independently reviewed persistent additive
schema rollout, old-writer drain and production-derived Aspire remain activation
gates, not evidence supplied by controlled SDK/disposable PostgreSQL tests.
This scope permits integrating write-disabled code, not activating storage or
applying a migration. No deployment or persistent/cloud action occurred.

## Current status — reviewed recovery runtime validated; frozen for root review

Root has now released the phase-two runtime gate: complete-batch metadata/compensation
fences, retained compensation dispositions, exact unique committed scan provenance for
PUT, and uncached physical capacities/validated constraints are implemented alongside
phase one and the shared immutable snapshot. This remains component acceptance only:
no deployed schema, malware certification, live GCS, IAM, activation or automatic
repair is claimed. Historical HELD/scaffold descriptions below record earlier phases,
not current runtime scope. No commits or pushes have been made.

Final private Release: zero warnings/errors. Final affected focus 112/112 PASS;
whole unfiltered suite 557/557 PASS, zero skips. All 86 new recovery cases pass.
The preceding 5-failure diagnostic is retained: historical move fixtures had metadata
and live bytes but no prior committed clean generation lineage. Root approved narrowly
preparing that prerequisite while preserving every failure/unknown/deletion assertion
and verifying the prior journal unchanged. Five explicitly approved
known-signing policy expectations now test generation-bound compensation, distinct
Absent, independent cancellation, all retained causes and zero name-only deletion.

## Historical phase-one status — retained chronology

Root approved only: distinct quarantine authority before initial RPC, required physical
recovery shape admission, forward-only migration refusal, and safe false-result tuple
controls. Runtime now contains those changes and the subsequently reviewed shared
whole-batch snapshot in this owned worktree. Signing and PUT runtime remain held.
Phase-one terminal: combined focus 104 = 96 GREEN/8 held RED; whole suite 511 =
503 GREEN/8 held RED, zero skips; independent phase-one suite 502/502 PASS (all
471 historical tests plus 31 phase-one controls). Nine separately held phase-two
cases were excluded only from that explicitly labeled independent denominator.
Release 0 warnings/0 errors. All five historical signing-preservation expectations are
still unchanged. Initial TEST/DESIGN-only scope and counts below are retained history,
not a description of current runtime or authorization.

Phase1 implementation: IQuarantineUploadIntent is mandatory on FileApplicationService,
registered only with existing enabled+writes flags; disabled implementation fails closed.
QuarantineUploadIntent is separate, non-expiring authority with per-object UUID,
parent request UUID, private coordinates, caller-declared type/length, optional positive
acknowledged generation, Pending/Uploaded/Unknown stage and immutable CreatedAt.
It contains no ScanClean claim. Duplicate operation identity never starts another RPC.
Both recovery tables' required columns/types/nullability and named primary/check
constraints are checked before intent insertion/provider mutation. This is explicit
admission, not an observation of deployed schema or blanket drift certification.
Initial acknowledgment/checkpoint failure retains intent and returns typed redacted503;
unknown checkpoint has an independent two-second cancellation bound. The new migration
`20261001023704_AddQuarantineUploadIntent` and existing move migration refuse Down.
No production DDL was applied.

Approved old-test changes are only required constructor fixtures, migration inventory,
and scope of the historical InstantQuote rollback test to its original migrations.
The new actual recovery downgrade test verifies refusal plus rows/history intact.
All six stronger-throw tuple proposals were explicitly deferred: BeginAsync false is
safe rejection; new controls verify all six winner fields, generation/state and dates
remain unchanged. No journal runtime change was made for collision semantics.

## Pending phase 2 exact design (NOT runtime authorization)

### Exact compensation/metadata/provenance gate — TEST/DESIGN only

Observed gaps are not inferred from fake success: actual PostgreSQL before-AddRange
observer records SourceDeleted, and removing one durable batch member at signing still
allows repository submission/committed rows. Actual UnknownAsync overwrites all four
compensation states plus MetadataSubmitting. Physical State varchar8 admits initial RPC
before journal failure; a same-named SourceGeneration CHECK(TRUE) admits the workflow.
Both drift tests roll back disposable fixture DDL in finally. Duplicate identical
committed evidence and conflicting Unknown47/Committed31 both permit current PUT204.
`compensation-fence-shape-put-red.trx`: eleven genuine RED, zero skips/errors.

Proposed internal methods on the existing IStorageMoveJournal boundary (not wire DTOs):

- TryBeginMetadataSubmissionAsync(exactClaims, token): complete-batch SourceDeleted ->
  MetadataSubmitting atomically BEFORE invoking AddRange; failed/uncertain claim makes
  zero metadata submissions and preserves every destination.
- TryBeginCompensationAsync(exactClaims, token): complete-batch SourceDeleted ->
  CompensationPending atomically BEFORE any conditional destination delete.
- MetadataSubmissionCommittedAsync(exactClaims, token): only MetadataSubmitting ->
  MetadataCommitted after successful AddRange acknowledgment. Lost metadata ACK leaves
  MetadataSubmitting retained, never eligible for compensation or automatic retry.
- RecordCompensationAsync(exactClaim, disposition, token): only matching
  CompensationPending and exact positive destination generation -> CompensatedRemoved,
  CompensatedAbsent or CompensationUnknown. A checkpoint failure leaves its last durable
  authority intact and is retained with original signing/cleanup causes.
- FindCommittedSourceAsync(bucket, exactName, token): null/unavailable unless exactly one
  journal record exists for that destination, with ScanClean=true, MetadataCommitted,
  positive source/destination generations and immutable coordinates. Identical duplicates,
  contradictory generation/state/scan, or any second row reject; never sort by latest.

ExactClaim contains operation UUID, ScanClean, source bucket/name/positive generation,
destination bucket/name/positive generation. Reject empty/duplicate IDs and malformed
claims before querying. For both competing batch admissions, one PostgreSQL transaction
locks all operation rows in deterministic UUID order; validate every exact tuple, scan,
positive generation and SourceDeleted state plus absence of matching Upload metadata
before changing ANY row. One mismatch returns false with every row unchanged. Updates
and commit are all-or-none. Database/provider ambiguity is never converted to a successful
claim; commit-response loss means zero provider cleanup and retained pending authority.
No automatic transaction replay after unknown provider/commit effect. Tests must exercise
two independent real PostgreSQL contexts competing metadata versus compensation claims,
duplicate/missing/changed tuple, rollback, caller cancellation and commit lost ACK.
Those direct typed-method tests require reviewed compile shapes because methods do not
currently exist; current tests exercise real application/repository flow and dispositions.

SourceDeleted signing catch is narrow: after all acknowledged promotions, while no
metadata submission was attempted. It must claim the complete batch before deleting
any destination. Cleanups use only recorded generation31 and one independent bounded
token, attempt every eligible member, retain original cause plus every provider/checkpoint
cause internally. Returned false from the actual adapter means confirmed404 ->
CompensatedAbsent, not Removed. Generation47 replacement or lost delete ACK ->
CompensationUnknown; preserve replacement, never retry automatically. Unknown copy,
source deletion or metadata submission never reaches this signing-compensation branch.

Public errors remain fixed redacted503. If cleanup/checkpoint fails use the existing
UploadRollbackException.UploadFailure and AggregateException contract internally;
otherwise wrap the original signing cause in a fixed-message UploadOutcomeUnknownException.
Controller already emits generic503 for rollback; never expose/log Message, object
coordinates, provider text or causes. No new public response fields/routes are needed.

No additional journal column is required for these distinct states: modeled varchar32
fits them, has no state allow-list today, and all original tuples/generations/dates remain.
Physical admission must verify actual varchar capacities, validated canonical positive/
positive-or-null CHECK definitions and exact UUID PK columns, not names/kinds alone.
Read pg_catalog canonical PostgreSQL18 definitions from reviewed migrations; drift is
unavailable/zero initial provider effects. No repair DDL/automatic migration is performed.
If a state allow-list is later chosen, its additive forward-only migration and physical
readiness must be reviewed separately, not assumed from an EF model or history row.

Existing public move has a distinct SourceDeleted -> MetadataCommitted checkpoint after
its metadata MoveAsync, with no required-signing phase; do not silently reclassify that
legacy method as an upload compensation authorization. Upload uses the new whole-batch
metadata fence/commit methods. UnknownAsync may alter only original uncertain move stages;
it cannot overwrite MetadataSubmitting, MetadataCommitted or any compensation state.
Reconciliation may read MetadataSubmitting only with actual committed Upload metadata and
same-generation live evidence; never resubmit AddRange on absence/uncertain commitment.
Old binaries can bypass the new metadata fence: rollout requires old-writer drain and
exact schema readiness before activation. This code/design does not authorize activation.

PUT requires existing metadata plus the unique committed proof, then live generation
matching that proof and journaled move using that exact expected generation. Historical
metadata without proof is explicit unavailable, not trusted backfill. Existing routes,
permissions, successful same-generation control and metadata uniqueness are unchanged.
No fresh schema, live GCS/IAM/scanner proof or whole source-owner completion is claimed.

### Snapshot RED chronology and approved shared implementation — 2026-10-01

At this first gate no snapshot runtime was started. `snapshot-pre-runtime-reviewed-red.trx` is terminal
6 intended RED/2 cap GREEN, zero skips/errors: changing uploaded `[1]` versus scanned
`[2]`, reopened writable streams, canceled capture already starting one provider RPC,
declared1/actual0 and2, and normal RS256 Production HTTP two concurrent provider entries.
Exact100MiB generated synthetic bytes and over100MiB pre-open rejection both pass.
The earlier cap test's ArgumentException assumption was corrected to the existing
FileUploadValidationException contract; its diagnostic is retained, not product RED.

Root subsequently found the keyed coordinator fingerprints the caller BEFORE the
application upload. An application-only snapshot is therefore insufficient: keyed
HTTP digest/SDK/scanner continuity and whole-path single-open are not proven by the
direct service tests. Runtime authorization was paused pending a keyed HTTP RED and
shared-capture design. At that time ExecuteAsync callbacks captured original uploadFiles;
they must receive the captured list explicitly, not continue closing over the caller.

Revised bounded design: singleton one-slot **batch** capture (all immutable metadata
and bytes <=100MiB aggregate) before fingerprint/acquisition or provider RPC. Keyed
and unkeyed controller/coordinator execution receive that exact owned list; direct
service callers use the same capture component. Internal already-owned batches are
recognized without recapturing; arbitrary public IUploadFile cannot bypass capture.
The application releases/clears the batch after all upload/scan/promotions and before
signing; coordinator/controller finally is an idempotent safety release on replay,
conflict, acquisition fault, cancellation or reconciliation. Fingerprint uses the same
bytes and metadata that the SDK/scanner consume. No public route/DTO change follows.
This requires narrow coordinator callback/controller changes, not just application
capture. It bounds only added memory, not ASP.NET multipart buffering or whole process.

Keyed HTTP proof now executed: actual RS256 auth, real PostgreSQL repositories and
isolated real RedisUploadIdempotencyStore; test-only adversarial form replacement after
normal multipart parsing supplies `[1]`, then `[2]`, then `[3]`. Read the durable Redis
checkpoint independently: literal field bytes+`[1]` digest and completed state match;
same-key changed payload409 has no additional SDK effects. SDK `[2]` versus expected
`[1]` is genuine RED; scanner `[3]` and three initial caller opens are also guarded.
`snapshot-keyed-red.trx`: 7 RED/2 cap GREEN, zero skips/errors. Strengthened cancellation
test rejects timeout and requires actual caller-token OCE; current unknown wrapper is
genuine RED. Controlled SDK/scan acknowledgments remain component evidence only.

Exact proposed callback ownership: ExecuteAsync execute/reconcile delegates gain an
explicit captured IReadOnlyList<IUploadFile> argument alongside generation/path/token;
UploadsController passes this argument to service rather than closing over original
uploadFiles. Coordinator owns capture on every keyed/unkeyed entry, before fingerprint;
batch stores immutable copied file metadata and private exact buffers. Application owns
the critical-stage release after the last promotion and before required signing. Replay,
conflict, unknown reconcile, acquire failure and caller cancellation retain coordinator
finally disposal; idempotent Dispose cannot return the singleton slot twice. Direct
FileApplicationService entry obtains the same batch component, but an internally issued
batch is accepted without recapture; caller implementations cannot forge that type.
Fingerprint/SDK/scanner each receive fresh readonly non-public-buffer streams. Lease
release clears buffers but leaves immutable metadata available for signing/response.
Tests must verify replay disposal and cancellation permit a subsequent request, and a
second request can capture while the first is blocked at signing (release-before-signing).
No coordinator callback or snapshot implementation was changed before these REDs.

Root reviewed those observations and approved whole-batch runtime. Additional required
`snapshot-batch-pre-runtime-red.trx` is terminal 12 RED/2 cap GREEN: malformed/aborted
second-file zero-effects and keyed replay/store-fault single-capture disposal cases.
Current implementation is UploadSnapshotCapture/UploadSnapshotBatch, mandatory singleton
shared by coordinator/application. It captures metadata once, validates aggregate limits,
reads each exact length plus EOF before any RPC, returns internal readonly-stream wrappers,
clears/drops private arrays and releases capacity once. Coordinator fingerprints these
bytes and passes the captured list explicitly through both callbacks; controller consumes
that list. Application releases after all promotions, before signing; coordinator finally
releases on every replay/conflict/acquisition/reconciliation/cancellation path.
Fresh Release 0W0E; `snapshot-runtime-final-focus.trx` 52/52 PASS, zero skips, including
15 snapshot cases and 37 historical coordinator/application/controller controls. Actual
Production HTTP signing-block control proves a second request is admitted before the
first required-signing callback completes. This is component evidence, not GCS readiness.

Two individually approved historical fixture adaptations retain assertions: concurrent
coordinator callback explicitly disposes its internal captured batch at postpromotion/
metadata-wait stage, preserving InProgress409; independent-cleanup test cancels inside
the actual scanner invocation rather than before capture, retaining exact Unavailable
exception and uncanceled cleanup, strengthened with one exact generation17 attempt.
The pre-canceled fixture diagnostic remains `snapshot-runtime-focus.trx` (50 PASS/1
fixture conflict), not new product RED. No signing-policy expectations were changed.

Signing cleanup fault test correction: existing UploadRollbackException deliberately
sets UploadFailure to the original cause and InnerException to an AggregateException
containing original plus cleanup causes. The NEW test now asserts that existing contract;
it does not demand a product change to replace the aggregate with the signing exception.

Preparation diagnostic: an audit command's implicit restore omitted private properties
(environment was process-local), and a subsequent failed compile printed sibling shared
Defaults/Contracts Debug outputs as well as private ones. No source edits were made there;
zero shared-output effects cannot be claimed for that diagnostic. Corrective explicit
private restore and fresh Release 0W0E print only owned .dependencies paths. Subsequent
audit/format commands must set absolute private environment in the same invocation.

### Frozen bytes before any provider effect

The genuine changing-stream RED uploads literal `[1]`, then real scanner consumer
reads `[2]` from the caller's second OpenReadStream; current code proceeds to copy and
required signing. The corrected acceptance test expects one caller open, SDK `[1]`,
scanner `[1]` and a clean promotion. A snapshot fix should not reject original clean
bytes merely because a hypothetical second caller stream would differ.

Recommended `ImmutableUploadSnapshot` is an application-private disposable IUploadFile
wrapper: capture once into a privately owned exact-length byte buffer, require positive
declared length within the remaining 100MiB aggregate cap, read exactly that count and
check EOF with one extra-byte read. Too-short/too-long input fails before any provider
effect. Metadata is copied once. Fresh streams are non-writable and do not expose the
backing array. SDK upload and scanner consume that same immutable buffer; never reopen
the caller stream. Dispose/clear the buffer after upload+scan+promotion; cancellation
before RPC propagates, after uncertain RPC follows durable unknown preservation.

Resource proposal requiring root acceptance: singleton fail-closed one-snapshot slot,
acquired before allocation, at most 100MiB added live snapshot bytes plus a small read
buffer across admitted workers. Busy admission returns redacted503 rather than allocating
or waiting unboundedly. Hold the slot through upload/scan/promotion, release before the
later signing/metadata phase. This serializes that critical stage and reduces throughput;
it bounds only added snapshot memory, not ASP.NET's existing multipart buffering or the
entire process. No ArrayPool ownership ambiguity, unprotected file/row spool, config
grant or new infrastructure. A protected temporary-file alternative would need explicit
owner-only ACLs, deletion/cancellation policy and host resource acceptance; not selected.

Tests before implementation: changing stream RED already observed; declared-size mismatch,
100MiB exact/over-cap, caller abort, busy-slot denial and released-slot reuse must protect
zero pre-RPC effects and cleanup. Genuine clean/infected/unavailable scanner behavior and
public upload cap remain unchanged. No byte snapshot is scan certification on its own.

### Definite required signing compensation only

Use a narrowly scoped catch around required signing, after all promotions and before
the first metadata submission. Verify every promotion's durable evidence is SourceDeleted,
ScanClean and exact source/destination coordinates with positive destination generation.
Only then conditionally delete each recorded destination generation under one independent
bounded cleanup token. Never use name-only delete. If any evidence is absent/ambiguous,
preserve ALL promotions rather than compensate a subset. Retain original signing cause
and every cleanup failure plus durable coordinates. Record rollback disposition without
inventing metadata commitment/automatic retry; the exact disposition schema must be
reviewed before adding state. Generation47 replacements survive attempted generation31
cleanup. Readiness/database/checkpoint ambiguity is not proof cleanup succeeded.

The catch must not enclose initial upload, copy, delete or repository AddRange. Mark
metadata submission before awaiting it; a real commit followed by lost acknowledgment
is always preservation, never signing rollback. New real PostgreSQL commit-then-lost-ack
controls (both caller states) retain both rows, both copied generations and original cause.
Second-file copy lost-ack retains first confirmed plus second uncertain destinations.
Second-file definite signing failure has two sign calls/two promotions/no metadata and
is genuinely RED because generation31 cleanup attempts are zero. These controls are
component SDK acknowledgment simulations, not successful real-provider signing proof.

Exact proposed old test deltas, NONE applied yet:

| Existing FileApplicationServiceTests method | Required individually reviewed delta |
| --- | --- |
| UploadAsync_SignedUrlFailure_PreservesPromotedGenerationsForReconciliation | Rename to confirmed pre-metadata compensation; two conditional generation31 attempts, zero AddRange, original signing failure retained, no name-only cleanup. |
| UploadAsync_SignedUrlFailure_DoesNotDeleteOnCancellation | Rename to independent bounded conditional compensation; retain canceled caller and uncanceled cleanup assertions, add exact generation31 attempt rather than empty deletes. |
| UploadAsync_SignedUrlFailure_DoesNotAttemptNameOnlyRollback | Preserve no-name-only guarantee; expect typed rollback failure containing original signing plus configured conditional cleanup cause and zero metadata. |
| UploadAsync_MultiplePromotions_PreservesEveryObjectOnSigningFailure | Rename to attempt every confirmed generation; retain original signing cause, three exact-generation attempts and both configured cleanup failures, no metadata. |
| UploadAsync_SigningFailure_PreservesOriginalCauseWithoutDelete | Reconcile the fake DeleteResult=false semantics explicitly: actual adapter false means404 absent, not arbitrary unknown success; retain original signing cause and exact-generation-only cleanup. Do not silently claim false proves deletion. |

Unknown copy/delete/metadata preservation assertions and source-generation guards remain
untouched. Source254's definite signing policy is retained conceptually, but its unsafe
name-based cleanup is not ported.

### PUT generation-bound committed scan provenance

Require existing clean metadata AND durable MetadataCommitted journal evidence whose
destination matches the exact source bucket/name, ScanClean=true and both generations
positive. Reject ambiguous/conflicting authoritative records. Read live provider evidence
and require generation equals the committed destination generation. Then invoke the
existing journaled move with that exact expected generation and clean evidence; adapter
re-read/fencing catches drift between proof and copy. No null expected generation or
synthetic ScanClean from name-only Exists. Absent historical evidence is explicit redacted
503/no mutation until separately authorized trusted scan/import; no invented backfill.

New normal Production HTTP control with committed generation31 succeeds and checks
copy SourceGeneration=31, source removal, target metadata and committed move checkpoint.
Drift47 despite committed31 is genuinely RED (currently204); expected503/zero copy-delete/
unchanged metadata and journal. Name-only absence remains RED. Public routes/DTOs,
permissions, signed-read wire shape and broader read-provenance policy are unchanged.

Future bounded files: application snapshot/budget + FileApplicationService, IFileBoundaries
read-only committed-evidence query and StorageMoveJournalRepository, necessary exact
generation compensation adapter only if existing behavior insufficient, and individually
reviewed tests/docs. No worker, cloud/IAM, Upload schema or public contract expansion.

## Historical initial test/design authority (2026-10-01, superseded above)

TEST/DESIGN ONLY. Root assigned this unique worktree at reviewed PR42 head
`bf101088e5fa2e7d1f52e468f8b82ccc50dabc07`, branch
`codex/file-storage-recovery-acceptance-20261001`. No runtime, interfaces,
migrations, configuration, existing tests, canonical files or PR42 files were edited.
Final reviewed-output stage: Release 0 warnings/0 errors; new focus 16 RED/9 GREEN;
full suite 16 RED/480 GREEN, 496 total, zero skips/errors. All 471 existing tests pass.
Outputs are terminal and released to root; runtime authorization remains pending.
The new tests intentionally do not pass. They propose observable recovery contracts
for root review; they are not feature acceptance or permission to repair runtime.

The coherent outcome is durable, generation-bound recovery authority throughout
the private upload -> complete scan -> promotion -> required signing -> metadata
sequence. Unknown storage effects remain private and preserved. Confirmed effects
with a definite failure are separately eligible for exact-generation compensation.
No automatic repair, deletion sweep, provider activation or source-owner closure follows.

Owned files are only:

- `Legacy.Maliev.FileService.Tests/Integration/FileStorageRecoveryBoundaryTests.cs`
- `docs/file-storage-recovery-design-20261001.md`

Private clean dependency clones are independently copied, not linked outputs:
Defaults `8f4f5f27b226ffe406c4c79b1903742e8c2e7dd3`, Contracts
`78e48ffc4ee000df0510cba5e7c7a3c4c4d539d7`. Exact CI action is
`73dd7304ffe85ec504389fd7664cc39070b9f148`; SDK dependency upgrade PR43 is separate.
All fixture state is ephemeral PostgreSQL 18 Testcontainers state, including one
uniquely named database created/dropped inside that container. No production SQL,
live GCS, IAM, WIF, provider credentials, notifications or persistent data were accessed.

## Existing acceptance is bounded

Canonical main `3e31bc4c2fc951e4382e26bf128acb3163ed8eee` exact-main run
`36504803738` succeeded. PR42 candidate run `36727024455` succeeded with Release
0 warnings/0 errors, 471 passed/0 failed/0 skipped, format and package audit passing.
These are inspected CI results, not this lane's runs. PR42 has no formal approval
review and explicitly remains held for provider and parent-policy gates.

Actual new HTTP tests use the unchanged Production entry point, normal RS256 JWT
signature validation and granular permission handler. No fake auth handler, policy
evaluator, live-check bypass or IAM-success substitute is installed. RSA keys are
generated ephemerally in memory; wrong-key, missing-grant and anonymous negatives
guard admission. Unkeyed requests use a strict unused idempotency transport so no
fabricated upstream checkpoint success can satisfy a test.

Only external GCS SDK calls and scanner verdicts are controlled. SDK fixtures apply
synthetic generations 17 -> 31 and optionally replacement 47, then lose acknowledgments.
The real GoogleCloudObjectStorage adapter, application, controller, middleware and
PostgreSQL repositories execute. The scanner consumes all fixture bytes before its
controlled clean result. None of this demonstrates real provider permissions,
signBlob, conditional generations, actual malware detection or cloud readiness.

## Source cohort and historical comparison

Read-only bare mirror checkpoint:
`bed10c7d15e0698e0b75f1329d0f312937f5d77f`.

| Individual source SHA | Owning source paths / disposition |
| --- | --- |
| `25418c95b5ac79400029ce274541f0e51728da3e` | `Maliev.UploadService.GoogleCloudStorage/GoogleCloudStorage.cs`, `SignedUrl.cs`, `Maliev.UploadService.Tests/StorageFailureBehaviorTests.cs`: required signing before metadata; definite signing failure removes uploaded object, retains signing plus cleanup causes. Remaining parent13 policy. |
| `4533669fa5231368f17c4b59b17c3e2f52e24a89` | Upload deployment/service-account/script and Web deployment contract: WIF intent; independent operational acceptance, not reproduced here. |
| `56014f2efc7c7f24b777c51d17a454e023d12bdd` | Credential provider/injection: retain approved ADC/WIF target architecture; do not copy inline credentials or source identity. |
| `f0640fe0719b2eb6becda378bff08153d955be07` | Request incident/privacy: File owner already migrated; issue29 still has operational gates. No duplicate tracing implementation. |
| `9e51e6c5da29de8e617b65b59d46882cde6d3b64` | Native logging/LoggerService removal: File owner evidence pending, separate from storage recovery. |
| `5ac7d045c51194edd9e64d8564f1b726b001be34` | Application-local native logging/project/startup/docker: pending File owner evidence, no unrelated logging rewrite. |
| `b673b3692b998f704e08867e1d561bef76dcafe7` | 100MiB upload cap: accepted baseline, excluded rework. AGENTS' 200MB wording is stale. |
| `03dc9a1271c16e6535934445e9dd6e3f30e8fffe` | Generated XML isolation: later storage-path history, not a changed storage algorithm. |

Latest committed storage behavior still retains source254 signing compensation.
Source MoveAsync catches broadly and source signing cleanup deletes by name: those
unsafe mechanics must not be copied. Canonical 3e31 distinguishes definite
pre-metadata failure from ambiguous metadata outcome, but its cleanup is name-based.
PR42 instead preserves every promotion when any later exception occurs, including
definite signing failure with a fully recorded SourceDeleted/destination-generation
checkpoint. Existing tests explicitly require preservation; they remain untouched.

## Concrete findings and proposed decisions

### 1. Durable authority must precede initial upload

FileApplicationService uploads quarantine at line 60; tracking happens at line 63,
and the move journal is created only after scan/promotion begins. Lost acknowledgment
can therefore leave a private object without durable recovery coordinates. Normal
Production HTTP currently responds 500 for this controlled lost acknowledgment.

The current StorageMoveJournal schema requires a positive source generation, so it
cannot represent an unacknowledged initial upload. The tests query existing durable
coordinates to expose that gap; they do not authorize fabricating generation 0 or
assert a final new-schema interface. Before runtime, review a distinct additive
per-object quarantine-intent contract with operation/request identity, exact private
coordinates, content identity, unknown generation until acknowledged, and timestamps.
Intent commits before RPC; failed intent/readiness means zero cloud mutations.
Acknowledged upload attaches positive generation. Lost acknowledgment remains unknown
and cannot scan, promote, sign, insert metadata or trigger name-only cleanup.

Recommended approach: separate quarantine intent instead of weakening known-generation
move evidence. Alternative: extend one journal with explicit discriminated operation
and nullable generation states; that has a larger compatibility/migration surface.
Redis keyed request state is not a replacement for durable per-object PostgreSQL
authority; unkeyed uploads require generated durable operation identity too.

### 2. Tuple binding is explicit, not merely duplicate operation ID

BeginAsync currently returns false for both identical replay and a six-field mismatch.
It does not overwrite the winner, which is a valuable existing fail-closed property.
New tests propose explicit conflict rejection for source bucket/name/generation,
destination bucket/name and scan verdict; identical replay remains false and preserves
copied generation/state/CreatedAt/ModifiedAt. These six REDs are a proposed stronger
contract, not evidence that current false results authorize a copy.

A future narrow typed application exception/result must distinguish tuple conflict
from verified replay without importing EF into Application. Concurrent same-ID callers
must re-read the committed winner after INSERT conflict, never reset it. Existing
StorageMoveJournalTests deliberately expect false for conflicting tuples; changing
that test contract requires root approval, not blanket updating tests.

### 3. Recovery authority survives rollback; schema gates precede effects

AddStorageMoveJournal.Down drops the non-expiring journal. The new test executes actual
generated Down SQL inside a PostgreSQL transaction, confirms destruction, and rolls
the DDL back even on assertion failure. No shared fixture authority is left dropped.
Missing physical journal is separately tested in a fresh private container database
at the earlier migration: POST currently performs its initial cloud upload before
the eventual missing-table failure.

Recommended rollback policy is fail-closed refusal of journal downgrade while
retaining its table, rows and migration authority, with forward-only recovery
instructions. A silent no-op Down is not sufficient: migration history could claim
absence and later Up attempt to recreate an existing table. Existing rollback tests
assume permissive downgrade and need individually reviewed expectation changes.
Schema availability/readiness must be verified before admission/provider effects;
unknown database errors remain unavailable, not assumed success or empty history.

### 4. PUT cannot derive scan provenance from name-only metadata

Current MoveAsync passes expectedSourceGeneration=null and scanClean=true after
UploadRepository.ExistsAsync(bucket,name). Normal Production PUT with a historical
Upload row and no generation scan evidence returns 204, copies/deletes the provider
object and records a clean journal. This is not generation-bound scan proof.

Recommended admission: require committed complete-scan evidence for the exact live
source generation, otherwise redacted 503 with no copy/delete/metadata mutation.
Rows with absent evidence stay unavailable for this mutation until an explicitly
reviewed trusted re-scan/import path supplies evidence. Do not silently certify
historical rows, invent public DTO fields or claim the legacy Upload schema carries
generation authority. Generic signed-read name-only provenance is a related residual
outside the current tested PUT repair, not silently accepted by this bundle.

### 5. Confirmed signing failure differs from unknown storage outcome

New component tests reach required signing after durable generation 31 and confirmed
source deletion. Definite signing failure occurs before repository AddRange. Proposed
compensation deletes only destination generation 31 with generation preconditions,
uses an independent bounded token despite caller cancellation, and never deletes a
replacement generation 47. Conditional cleanup failure must retain original signing
cause, cleanup causes and durable coordinates. Existing preservation tests represent
the opposite policy and must be reconciled explicitly before runtime implementation.

Unknown copy/delete acknowledgments—including concurrent caller cancellation—remain
preserved, never sign/commit metadata, and record the last known generation. No
destination cleanup follows uncertainty. Unknown metadata commit response must also
retain promoted data; additional metadata-ack fault coverage belongs before any
future compensation repair. No automatic retry/reset/reconciliation mutation is
authorized. Signing transport is never fabricated as live provider acceptance.

## Review focus and future file scope (NOT WRITE AUTHORIZATION)

- Pre-RPC intent versus database outage/lost upload acknowledgment: Application
  FileApplicationService/IFileBoundaries, Domain/Data intent plus additive migration.
- Tuple conflict versus identical replay/races: StorageMoveJournalRepository,
  application-facing conflict type and adapter handling.
- Journal downgrade/readiness: reviewed migration/retention strategy, runtime
  registration/readiness and runbook; existing rollback expectations need approval.
- PUT historical/changed generation: FileApplicationService, UploadRepository read
  evidence boundary and storage adapter; no legacy Upload mapping/public-route change.
- Definite signing failure versus unknown metadata/copy/delete: FileApplicationService,
  exact-generation adapter cleanup, typed failure evidence and existing tests after
  root policy review. Never broaden catch-and-delete.

Historical initial implementation sequencing before root review (superseded by the
reviewed phase-one/snapshot/phase-two approvals and current summary above):

- [x] Approve authority/schema/retention and historical-row admission decisions.
- [x] Add exact typed intent/replay tests after interface approval; no inert runtime remains.
- [x] Implement minimum authority/readiness before provider mutations; observe GREEN.
- [x] Implement provenance and separate confirmed-signing compensation; add metadata
  lost-ack/replacement/race/multi-file faults before repair; observe GREEN.
- [x] Release 0W0E, focused/new/existing suites, complete affected suite, format/audit/
  redacted secrets, normal HTTP permissions and real PostgreSQL migration gates.
- [ ] Root independent acceptance, then separately authorized disposable real-GCS/WIF
  acceptance; no merge or parent13/41 completion inferred from component GREEN.

## Commands and chronological evidence

Working directory: `B:/maliev-legacy/.worktrees/file-storage-recovery-acceptance-20261001`.
Every command uses its absolute `.dependencies` root and sequential outputs.

```powershell
dotnet restore Legacy.Maliev.FileService.slnx -p:UseLocalMalievDependencies=true -p:MalievWorkspaceRoot=B:/maliev-legacy/.worktrees/file-storage-recovery-acceptance-20261001/.dependencies
dotnet build Legacy.Maliev.FileService.slnx -c Release --no-restore -p:UseLocalMalievDependencies=true -p:MalievWorkspaceRoot=B:/maliev-legacy/.worktrees/file-storage-recovery-acceptance-20261001/.dependencies
dotnet test Legacy.Maliev.FileService.Tests/Legacy.Maliev.FileService.Tests.csproj -c Release --no-build --no-restore -p:UseLocalMalievDependencies=true -p:MalievWorkspaceRoot=B:/maliev-legacy/.worktrees/file-storage-recovery-acceptance-20261001/.dependencies --filter 'FullyQualifiedName~GoogleCloudObjectStorageFailureTests|FullyQualifiedName~StorageMoveJournalTests|FullyQualifiedName~FileController|FullyQualifiedName~FileJwtExternalizationTests|FullyQualifiedName~PostgreSqlMigrationTests' --logger 'trx;LogFileName=baseline-storage-http.trx' --results-directory TestResults/baseline
dotnet test Legacy.Maliev.FileService.Tests/Legacy.Maliev.FileService.Tests.csproj -c Release --no-build --no-restore -p:UseLocalMalievDependencies=true -p:MalievWorkspaceRoot=B:/maliev-legacy/.worktrees/file-storage-recovery-acceptance-20261001/.dependencies --filter FullyQualifiedName~FileStorageRecoveryBoundaryTests --logger 'trx;LogFileName=recovery-red-03.trx' --results-directory TestResults/recovery-red-03
```

Initial Release baseline: 0 warnings/0 errors; focus 64 passed/0 failed/0 skipped.
First new build had two compile errors and an analyzer error, corrected only in
the new test. `recovery-red-01` 17 failed/4 passed included an SDK upload callback
arity fixture mistake and is diagnostic, not acceptance. `recovery-red-02` corrected
that callback: 14 genuine failed/7 passed. `recovery-red-03` added lost-ack HTTP and
missing-physical-journal cases: 16 failed/7 passed/0 skipped, 23 total, no test errors.
All failing results are retained. The final cancellation expansion tests four unknown
copy/delete scenarios (both uncanceled and canceled callers). Final Release after
scoped formatting: 0 warnings/0 errors. Final focus `TestResults/recovery-red-final/
recovery-red-final.trx`: 25 total, 16 failed, 9 passed, zero errors/skips. Whole suite
`TestResults/recovery-full-red/recovery-full-red.trx`: 496 total, 16 failed, 480 passed,
zero errors/skips; every failure belongs to the new test class. The 471 pre-existing
tests, including explicit signing preservation, collision false-result and rollback
controls, all remain GREEN and unchanged.

Final RED inventory: pre-upload authority/lost-ack durability 2; tuple-conflict
mutations 6; destructive Down 1; definite signing compensation/cancellation/
replacement/cleanup failure 4; Production HTTP lost initial response 1 (actual500);
physical journal absence before cloud mutation 1 (actual UploadCalls=1);
Production HTTP PUT provenance 1 (actual204). GREEN controls: exact replay/date
preservation 1, unknown copy/delete including caller cancellation 4, real HTTP
anonymous/wrong-key/no-grant/create-unknown fault controls 4.

Full test command is the documented test command without `--filter`, logger
`trx;LogFileName=recovery-full-red.trx`, results directory `TestResults/recovery-full-red`.
Final focus uses `--filter FullyQualifiedName~FileStorageRecoveryBoundaryTests`, logger
`trx;LogFileName=recovery-red-final.trx`, results directory `TestResults/recovery-red-final`.

Static commands were serialized after terminal tests, with environment
`MalievWorkspaceRoot=B:/maliev-legacy/.worktrees/file-storage-recovery-acceptance-20261001/.dependencies`
and `UseLocalMalievDependencies=true`:

```powershell
dotnet format Legacy.Maliev.FileService.slnx --verify-no-changes --no-restore
dotnet list Legacy.Maliev.FileService.slnx package --vulnerable --include-transitive
git diff --check
gitleaks git . --redact=100 --exit-code 0 --no-banner --no-color
Get-Content Legacy.Maliev.FileService.Tests/Integration/FileStorageRecoveryBoundaryTests.cs -Raw | gitleaks stdin --redact=100 --no-banner --no-color
Get-Content docs/file-storage-recovery-design-20261001.md -Raw | gitleaks stdin --redact=100 --no-banner --no-color
```

Historical test/design-stage format verification and diff check pass. NuGet audit reports no vulnerable packages
in all five projects. Gitleaks scans 80 commits and both new untracked files, no leaks
found. Both private clone working trees remain clean. No current output handles
remained at that initial TEST/DESIGN-only handoff; no runtime/migration/interface changes
or commits/pushes had yet been made. At that historical phase, actual provider acceptance,
authority schema/API design and old policy conflicts remained explicit root-review gates,
not skipped checks mislabeled as passing. Current phase approvals and final evidence
are recorded separately below and in the current summary.

## Phase-two runtime and configured retry evidence — 2026-10-01

`StorageMoveJournalRepository` locks complete claims in UUID order, validates exact
six coordinates/generations/scan state plus metadata absence before updating any row,
and commits the entire batch. MetadataSubmitting competes atomically with
CompensationPending. Required signing failures are compensated only after a confirmed
complete-batch claim; the acknowledgment of COMMIT alone is not assumed from effects.
Lost claim acknowledgment leaves every promoted generation untouched. Metadata
submission/unknown acknowledgment retains MetadataSubmitting rather than downgrading
it to Unknown. Removed/Absent/Unknown dispositions cannot be overwritten by generic
Unknown updates. Public move retains its separately bounded legacy checkpoint path.

The configured Production dependency registers Npgsql retry five times with a ten-second
maximum delay (`Extensions.Database.cs` lines 72/85 in the exact private Defaults pin).
Initial direct PostgreSQL controls did not expose the manual-transaction incompatibility.
Normal Production RS256 HTTP did: recovery focus 78 = 74 PASS/4 FAIL. New retry-enabled
pre/post-COMMIT controls reproduced two assertion REDs with the exact configured
strategy's user-initiated-transaction exception; no test setup error was counted as RED.

The repository now enters the registered execution strategy, but permits only ONE
guarded attempt. All transaction/query/COMMIT/disposal ambiguities are wrapped INSIDE
the strategy delegate in a fixed-message UploadOutcomeUnknownException, retaining the
original exception internally. No transient COMMIT retry, context substitution or retry
disable is used. Pre-invocation caller cancellation is unchanged. Additional genuine
transient Npgsql/Timeout, arbitrary InvalidOperation, and provider OCE post-COMMIT
controls prove one commit call, exact durable pre/post state, and no destination cleanup
or metadata. Two required quarantine17 source deletes occur during promotion BEFORE
the uncertain claim; the new overly broad zero-all-deletes assumption was corrected to
zero destination-generation cleanup with those exact two source deletes retained.

Evidence directories (all under this owned worktree's TestResults):

- `recovery-configured-retry-red`: 2 RED/0 GREEN, exact strategy incompatibility.
- `recovery-phase2-strategy-focus`: 80/80 PASS, including normal Production HTTP.
- `recovery-phase2-old-policy-diagnostic`: 33 PASS/10 FAIL, five approved known-signing
  policy deltas, one missing move-fixture provenance, four new source-delete count
  assumptions (not new product defects).
- `recovery-phase2-expanded-focus`: 107 PASS/5 FAIL, all 86 new cases pass.
- `recovery-phase2-whole-before-fixture-review`: 552 PASS/5 FAIL/0 skips, all 557 cases
  unfiltered; only five historical missing-provenance move fixtures remain.

The two new commit-boundary cases and component workflow controls use the same retry
parameters as Production. Real normal Production HTTP positives exercise actual host
registration, PostgreSQL18, normal RS256 permission middleware and controlled SDK faults;
they are not actual-provider readiness. A transient-fault implementation cannot blindly
replay an ambiguous claim, even if future strategy classification changes, because the
explicit second-attempt guard fails closed. Final whole/static gates remain required.

## Final component gate and handoff — 2026-10-01

The prior five missing-lineage fixture failures were not product failures or weakened
assertions. The unit test now performs a successful normal clean upload before its
faulted move, captures that committed journal, and proves it remains unchanged. The four
controller cases retain original generation17 source/delete404/checkpoint failure behavior;
their narrowly staged synthetic prior clean journal has quarantine sourcegeneration5,
destination livegeneration17, SourceDeleted→MetadataCommitted transitions, existing Upload
metadata and exact coordinates. Its full persisted fields/timestamps are unchanged after
the attempted new move. Missing/ambiguous/drifted lineage refusal remains covered by
normal Production RS256 HTTP new controls. No runtime admission bypass was introduced.

Final serial commands, with the private dependency properties above:

```powershell
dotnet build Legacy.Maliev.FileService.slnx -c Release --no-restore -p:UseLocalMalievDependencies=true -p:MalievWorkspaceRoot=B:/maliev-legacy/.worktrees/file-storage-recovery-acceptance-20261001/.dependencies
dotnet test Legacy.Maliev.FileService.Tests/Legacy.Maliev.FileService.Tests.csproj -c Release --no-build --no-restore -p:UseLocalMalievDependencies=true -p:MalievWorkspaceRoot=B:/maliev-legacy/.worktrees/file-storage-recovery-acceptance-20261001/.dependencies --filter 'FullyQualifiedName~FileApplicationServiceTests|FullyQualifiedName~FileControllerMoveCheckpointTests|FullyQualifiedName~FileStorageRecoveryBoundaryTests' --logger trx --results-directory TestResults/recovery-phase2-final-focus
dotnet test Legacy.Maliev.FileService.Tests/Legacy.Maliev.FileService.Tests.csproj -c Release --no-build --no-restore -p:UseLocalMalievDependencies=true -p:MalievWorkspaceRoot=B:/maliev-legacy/.worktrees/file-storage-recovery-acceptance-20261001/.dependencies --logger trx --results-directory TestResults/recovery-phase2-final-whole
$env:UseLocalMalievDependencies='true'
$env:MalievWorkspaceRoot='B:/maliev-legacy/.worktrees/file-storage-recovery-acceptance-20261001/.dependencies'
dotnet format Legacy.Maliev.FileService.slnx --verify-no-changes --no-restore
dotnet list Legacy.Maliev.FileService.slnx package --vulnerable --include-transitive --no-restore
git diff --check
gitleaks git . --redact=100 --no-banner --no-color
git diff --no-ext-diff | gitleaks stdin --redact=100 --no-banner --no-color
```

Results: Release 0W0E; focus 112 passed/0 failed/0 skipped; complete 557 passed/0 failed/
0 skipped. No exclusions, held REDs, fabricated provider success or positive-auth mocks
remain in this final denominator. Format verification passes. All five projects report
no vulnerable packages; diff check passes (only Git's CRLF normalization notices).
Gitleaks scans all 80 committed-history objects, current tracked diff and every new
untracked file with redaction, no leaks. No secret values or private object coordinates
are included in public error responses or new logs. Both exact private dependency clones
remain clean. Final evidence:

- `TestResults/recovery-phase2-final-focus/natth_MALIEV-31USFIV_2026-10-01_10_58_10_net10.0.trx`
- `TestResults/recovery-phase2-final-whole/natth_MALIEV-31USFIV_2026-10-01_10_58_58_net10.0.trx`

The branch remains uncommitted at bf101088e5fa2e7d1f52e468f8b82ccc50dabc07. All outputs
are owned by this worktree and are released/frozen for root independent review after
the final readback/secret checks. No original/canonical/old PR42 source changes, commits,
pushes, deployment or persistent/provider actions. Actual GCS/WIF/scan certification,
physical production rollout and old-writer drain remain separate operational gates;
parent13/41 and the entire source owner are not declared complete from these components.
