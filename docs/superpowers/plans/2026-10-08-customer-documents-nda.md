# Customer documents and NDA implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox syntax for tracking.

**Goal:** Deliver customer-scoped immutable evidence receipts, externally signed NDA verification and surviving protection, secure staff/member document journeys and internal reminder worklists.
**Architecture:** Distinct CustomerDocuments registry module in existing Legacy.Maliev.FileService, separate CustomerDocumentDbContext/additive tables, reuse existing storage/scanner/IAM adapters. No renderer persistence or new service/infrastructure. Existing owners integrate shared registration and adapters from narrowly reviewed patches.
**Tech Stack:** .NET10, existing EF Core10/PostgreSQL, GCS ADC/WIF, ClamAV, existing JWT/resource permissions, Blazor/same-origin BFF.
**Spec:** ../specs/2026-10-08-customer-documents-nda-design.md
**Execution authority:** https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.Intranet/issues/277#issuecomment-6053052177 read back from owner account. Advance approval covers feature code, not runtime/deployment/live side effects.

## Global constraints
- Existing legacy routes/Upload mappings unchanged except explicit guard for reserved protected objects; no live grants or production mutations.
- Customer scope must be authoritative; matching company/name/email never grants access.
- Immutable versions/clean generation evidence; expiry never releases confidentiality.
- Externally signed agreements only; no customer notices/e-signing/public sharing.
- Restricted metadata omitted from member results; current social checkbox/value unchanged.
- Disabled runtime/scheduler defaults; all fixtures synthetic and dependencies isolated.
- Shared File/IAM source paths owned by existing task owners; new-file work may proceed independently.
- Zero-warning Release build, TDD, PostgreSQL migration tests, full affected suites, independent review, protected exact-head/fresh-main verification.

## Review Focus
- Owner/tenant mismatch even when document/version IDs are valid must refuse before storage.
- Superseded or renewed NDA must not erase old protected derivative evidence.
- Lost upload/move response must retain exact generation and pending state without unsafe cleanup.
- Direct legacy signed-read/move/delete paths must not bypass reserved registry originals.
- Midnight/timezone/renewal-revision races must not duplicate or misaddress internal reminders.

### Task1: Immutable registry and evidence receipts
**New files (FileService):**
- Domain/CustomerDocuments/CustomerDocument.cs, CustomerDocumentVersion.cs, DocumentAssociation.cs, DocumentAudit.cs (prefix every path with Legacy.Maliev.FileService.).
- Application/CustomerDocuments/DocumentContracts.cs, ICustomerDocumentRegistry.cs, ICustomerDocumentAuthority.cs, ICustomerDocumentAssociationValidator.cs.
- Data/CustomerDocuments/CustomerDocumentDbContext.cs, CustomerDocumentModelConfiguration.cs, CustomerDocumentRegistry.cs, CustomerDocumentAssociationClient.cs.
- Data/Migrations/CustomerDocuments/20261008_InitialCustomerDocumentRegistry.cs and generated designer/snapshot for CustomerDocumentDbContext.
- Api/Controllers/CustomerDocumentsController.cs, Api/CustomerDocuments/CustomerDocumentRegistration.cs.
- Tests/CustomerDocuments/DocumentReceiptHttpTests.cs, DocumentRegistryPostgreSqlTests.cs, DocumentAssociationHttpTests.cs.
**Consumes:** Existing normal JWT/resource permission middleware and owner-confirmed authoritative customer/order/quotation reads. No local ownership override.
**Produces:** GET /customers/{customerId:int}/documents/{documentId:guid}/versions/{versionId:guid}/receipt -> DocumentEvidenceReceipt(Guid DocumentId, Guid VersionId, int CustomerId, DocumentKind Kind, string ContentSha256, int? QuotationId, IReadOnlyList<int> OrderIds, VerificationStatus VerificationStatus, string? VerifiedBySubject, DateTimeOffset? VerifiedAtUtc, long Revision).
Registry method: Task<DocumentEvidenceReceipt?> ReadReceiptAsync(DocumentActor actor,int customerId,Guid documentId,Guid versionId,CancellationToken token). Authority/associations validated before readback; unavailable503, notfound404, wrong scope403/404, malformed400; no override.
- [ ] Write tests requiring real registered HTTP refusal of cross-customer/version and unavailable authority, employee/member Internal visibility, immutable digest/actor/revision, canonical quotation/order mismatches; DB constraints/concurrent versions.
- [ ] Run focused tests and record RED (missing route404 or missing type compile failure separately; actual behavioral RED required before feature repair).
- [ ] Implement exact receipt/summary DTOs, separate registry DbContext/migration and authoritative readback, append-only audit; no storage path/URL output.
- [ ] Execute focused HTTP and real disposable PostgreSQL18 migrations/concurrency tests; verify GREEN and normal auth denial controls.
- [ ] Commit new files; independently review this consumer-ready contract before billing integration.

### Task2: Protected upload/version/download and archive
**New files:** Application/CustomerDocuments/CustomerDocumentUploadService.cs, CustomerDocumentDownloadService.cs, DocumentContentValidator.cs, CustomerDocumentOptions.cs; Data/CustomerDocuments/CustomerDocumentStorageAdapter.cs; Tests/CustomerDocuments/ProtectedDocumentUploadTests.cs, ProtectedDocumentDownloadHttpTests.cs, ProtectedDocumentBypassHttpTests.cs.
**Shared-path owner handoff:** FileServiceRuntimeRegistration.cs, FileApplicationService.cs, IFileBoundaries.cs, GoogleCloudObjectStorage.cs, DisabledFileDependencies.cs and Program.cs only via exact reviewed owner integration. No unilateral shared edits.
**Interfaces:** Task<DocumentVersionReceipt> UploadAsync(DocumentActor actor,int customerId,DocumentUploadRequest request,IUploadFile file,string idempotencyKey,CancellationToken token); Task<DocumentDownload> ReadAsync(DocumentActor actor,int customerId,Guid documentId,Guid versionId,CancellationToken token). Max20MiB; PDF/PNG/JPEG detection; metadata-only clean finalize; authenticated attachment proxy.
- [ ] Write tests for malware/all unavailable verdicts, mismatch/truncation/size, exact content hash/generation, unverified historical bytes, wrong tenant, concurrent/replayed content, lost-response quarantine/promotion and no URL creation.
- [ ] Run RED; implement reserved private object identities, durable upload state/idempotency, reuse full-file scanner/journaled generation move, commit immutable version+audit, authenticated bounded download.
- [ ] Write and observe bypass RED on direct legacy sign/move/delete for reserved prefix; hand narrow guard patch to owner, test registry never signs or returns bearer URLs.
- [ ] Test archive permission/reason/conflict/hold preserving originals; run all affected suites and exact migration checks.
- [ ] Commit, independent review, resolve load-bearing findings.

### Task3: NDA verification, persistent protection and reminders
**New files:** Domain/CustomerDocuments/NdaRecord.cs, NdaCoverage.cs, InternalNdaReminder.cs; Application/CustomerDocuments/NdaVerificationService.cs, DocumentProtectionService.cs, NdaReminderScheduler.cs; Api/Controllers/NdaVerificationController.cs, DocumentProtectionController.cs, InternalNdaRemindersController.cs; corresponding Data model configurations/migration; Tests/CustomerDocuments/NdaLifecycleTests.cs, DocumentProtectionHttpTests.cs, NdaReminderPostgreSqlTests.cs.
**Interfaces:** VerifyAsync(DocumentActor,int customerId,Guid documentId,NdaVerificationRequest,CancellationToken) verifies exact clean version with expected revision; EvaluateAsync(DocumentActor,int customerId,ProtectionRequest,CancellationToken)->ProtectionDecision; QueueDueAsync(CancellationToken)->int using TimeProvider and configurable lead days30/14/7/1/0.
- [ ] RED: member cannot verify; invalid dates/parties/responsible staff rejected; stale revision409; expired/unknown/indefinite/superseded agreements still protect linked derivatives.
- [ ] Implement distinct agreement/obligation state and explicit coverage; no release/override endpoint. Read allows authorized work, ExternalShare/PublishSocial/PublicExport denies protection independently of consent.
- [ ] RED/GREEN: protected work publication/export, unavailable owner response, wrong tenant, finite survival calendar cannot implicitly release, immutable verification audit.
- [ ] RED/GREEN reminder tests: responsible employee only; timezone midnight; same/different renewal epoch; concurrent unique task; cancelled stale future reminder; disabled scheduler; no Notification send/customer recipient.
- [ ] Run real PostgreSQL migration/concurrency, full affected suites; commit and independent review.

### Task4: Intranet and Web BFF/UI integration
**New Intranet paths:** Contracts/CustomerDocumentContracts.cs; Bff/CustomerDocuments/CustomerDocumentEndpointMapper.cs, CustomerDocumentProxy.cs; Client.Features.Customers/Components/CustomerDocuments.razor, CustomerNdaVerification.razor; Tests/CustomerDocumentBffTests.cs, BrowserTests/CustomerDocumentsBrowserTests.cs.
**New Web paths:** Application/CustomerDocumentContracts.cs; Infrastructure/CustomerDocumentClient.cs; Web/CustomerDocumentEndpointRouteBuilderExtensions.cs; Web/Components/Pages/Member/MemberDocumentsPage.razor, MemberDocumentsContent.razor; Tests/MemberDocumentsHttpTests.cs, MemberDocumentsBrowserTests.cs. Thai resources alongside existing component resources.
**Shared owner handoff:** CustomerView.razor, member navigation and Program/BFF registrations, OrderDetail/publication/export caller guards. Existing source owners integrate narrow patch; never change AllowSocialMedia.
**Consumes:** Task1 immutable receipts, Task2 clean upload/download, Task3 verification/protection/reminders. Server session derives member customer; CSRF and downstream current authority intact.
- [ ] RED/GREEN actual employee/member HTTP journeys with CSRF, hidden Internal metadata, no browser tokens/URLs, denied/stale/unavailable states and20MiB enforcement.
- [ ] RED/GREEN rendered/browser upload/version/download/verification/reminder journeys in Thai/English, keyboard use, expired-but-protected warnings and unchanged social consent.
- [ ] Inventory actual file/image/export/social producers and hand exact policy enforcement patch to their owners; absent/unavailable authority blocks protected output. Record unresolved producer as blocker, not completed enforcement.
- [ ] Build/test full Intranet/Web affected suites and browser isolated end-to-end with actual registry/backend. Commit and independent cross-service review.

### Task5: Protected integration and delivery evidence
- [ ] Refresh exact origin/main/active ownership without resetting owner changes; rebase isolated feature branches as needed.
- [ ] Full Release0W/E, dotnet format --verify-no-changes, vulnerable dependency audit, gitleaks, migration/HTTP/browser evidence; record failures by name.
- [ ] Fresh independent whole-feature review; fix important findings and rerun only affected checks.
- [ ] Publish feature-only design/plan and reviewed code through protected PRs; attach every PR to task; confirm required checks at exact head before merge. No rejected sensitive report publication.
- [ ] Verify exact fresh-main commit and CI; update#277 with actual deliverables/gaps. Close only after full named joined acceptance. No deployment/runtime activation.

## Self-review and execution ledger
Spec coverage: placement/ownership->Task1; bytes/version/retention->Task2; NDA/protection/reminders->Task3; employee/member/actions->Task4; delivery evidence->Task5.
Interface consistency: DocumentId/VersionId Guid, CustomerId positive int, SHA256 lowercase64hex, Revision positive long; explicit QuotationId and OrderIds receipt satisfies billing. VerificationStatus includes PendingVerification/Verified/Rejected; dates are UTC instants with explicit UI timezone.
Shared tasks:1 produces receipt consumed2/4;2 produces exact clean version consumed3/4;3 produces protection consumed2/4; all use same actor/customer identity. No cycle: registry receipt may be Pending; verification is separate mutation, protection evaluates stored records. The four shared source groups require exact owner handoff, not generic reservation.
Unresolved authority integration is a fail-closed blocker for executable joined acceptance. Internal worklist is first-release reminder delivery; no external sender added.
All steps currently pending; do not report unrun tests as passing.

