# Task 3B1 report

## Status

Implemented the bounded application-orchestration slice for instant-quotation file intake. No Google SDK adapter, production DI/Program registration, hosted cleanup service, external quotation-service call, or pricing behavior was added.

## RED evidence

- Session capability: the first focused run failed with CS0246 for the missing application service and storage/scanner boundaries.
- Upload owner boundary: the focused run failed because no owner-qualified `UploadAsync` overload existed.
- Upload reservation correction: conflict, in-progress, unknown, and replay tests were deliberately run after removing prematurely added branches; all four failed because storage was touched or no stable exception was returned. Minimal branches were then restored from the tests.
- Scanner fail-closed: Unsafe and Unavailable tests failed because both classifications were temporarily accepted.
- Unknown upload reconciliation: failed with `InstantQuoteAmbiguousOutcomeException` instead of reconciling deterministic metadata.
- Finalization exact-file read: failed with CS0246 for missing `InstantQuoteStoredUpload`.
- Cancellation: failed because the Unknown save used stale xmin 17 after Uploaded advanced it to 18.
- Finalization unknown reconciliation: failed because Unknown returned `outcome_unknown` instead of reconciling already-finalized links.
- Contract: failed for the missing five-field session response and finalized bucket/object DTO.

## GREEN evidence

Commands used the required workspace override and ran sequentially.

- `InstantQuoteFileServiceTests`: 22 passed. Covers token hash-only persistence, owner verification, upload reservation states, replay without I/O, unknown reconciliation, nonseekable single-pass upload, digest/size/signature checks, exact-generation scan, unsafe/unavailable fail-closed cleanup, cancellation with latest xmin, ambiguous storage outcome, finalization validation/ownership/state checks, exact promotion, replay/unknown reconciliation, partial retry, and promotion/state-save ambiguity.
- `InstantQuotationFilesContractTests`: 19 passed. Covers routes/headers, camelCase final wire shape, session limits/extensions, authoritative finalized links, and stable problem codes.
- `InstantQuoteOwnershipTests`: 13 passed.
- `InstantQuotePersistenceTests`: 11 passed, including exact session-owned file reads with observed xmin.
- Scoped `dotnet format ... --verify-no-changes`: passed.
- `dotnet build Legacy.Maliev.FileService.slnx -p:MalievWorkspaceRoot=B:\maliev --no-restore --verbosity minimal`: passed with 0 warnings and 0 errors.
- `git diff --check`: passed.

## Files

- Added application options, fakeable object-storage/scanner boundaries, orchestration service, and bounded signature policy.
- Updated application service/repository boundaries, response DTOs, controller owner propagation, problem mapping, PostgreSQL exact-file reads, contract documentation, and focused tests.
- Preserved the existing domain model and PostgreSQL shadow `xmin`; every state transition uses the version observed or returned by the immediately preceding save.

## Self-review

- Customer filenames are metadata only; temporary/final object names contain only opaque session/file/quotation IDs and validated extensions.
- Token plaintext is returned once and only SHA-256 is persisted; digest comparisons are constant-time.
- Upload bodies are streamed once into private storage and immutable generations are streamed through a pipe to the scanner without full-file application buffering.
- Scanner Unsafe/Unavailable and invalid digest/signature fail closed; ambiguous storage/state outcomes persist Unknown where an authoritative result cannot be proven.
- Finalization returns exact bucket/object links and retries avoid duplicate logical links by reusing already-finalized records.
- No price, amount, currency, pricing, calculator, credential, connection-string, or SDK-specific field/call was introduced.
- TDD correction: reservation branches were initially implemented before their explicit focused tests. They were removed, observed RED, and reintroduced minimally. This correction is retained here for auditability.

## Concerns and follow-up boundaries

- Production storage/scanner adapters, DI/Program wiring, and hosted cleanup remain intentionally outside Task 3B1.
- The object-storage boundary treats an empty bucket input as “the configured instant-quotation bucket” for reconciliation/promotion because bucket identity is returned by storage metadata and is not persisted in the existing upload domain row; the production adapter must implement that convention consistently.
