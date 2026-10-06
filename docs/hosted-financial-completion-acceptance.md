# Hosted financial completion File composition

This profile selects the actual Google storage SDK and actual
`ClamAvFileSafetyScanner` inside the dedicated
`HostedFinancialCompletionAcceptance` environment. Normal environments retain
the existing ADC/WIF storage registration, cloud signing origin and scanner
settings. Explicit profile configuration in another environment is rejected.

Admission requires Linux and the exact GitHub-provided
`RUNNER_ENVIRONMENT=github-hosted` classification. Self-hosted, absent or differently
cased classifications are rejected. The pinned validation action overrides
`GITHUB_ACTIONS` while selecting local dependency projects, so that boolean is
not the runner classification. Exact run, attempt, compiled source, resource and
finite lease checks remain required.

The admission delivery mechanism is ordinary .NET configuration, under
`HostedFinancialCompletionAcceptance:Enabled=true` and
`HostedFinancialCompletionAcceptance:Admission`. The dedicated launcher supplies
the nested values using environment keys with double underscores or a temporary
configuration provider. This is not a credential file. No credentials, customer
data or private signing key belong in this configuration or evidence.

Admission fields match the launcher owner's v3 source agreement:

| Field | Required value |
| --- | --- |
| schemaVersion | Integer 1 |
| runId | Canonical positive decimal `GITHUB_RUN_ID` string |
| runAttempt | Positive integer equal to `GITHUB_RUN_ATTEMPT` |
| fileSourceSha | Exact 40 lowercase hex commit from the built API's informational version |
| issuedUtc / expiresUtc | UTC offset zero, issued no later than now, current expiry, maximum 30 minutes total |
| storageOrigin | Canonical HTTP literal `127.0.0.1` or `[::1]`, explicit port; no credentials/path/query/fragment |
| storageEndpointIdentity | Typed process or container identity below |
| scannerHost / scannerPort | Same literal IP family as storage; admitted TCP port |
| scannerContainerId / scannerImageDigest | Exact observed 64 hex container ID and pinned `sha256:` image digest |
| scannerDatabaseIdentity | Loaded engine/database identity below |
| resourceLeaseId | Canonical nonempty `c821-<UUID>` fixture ownership lease |

A process storage identity contains kind=`process`, pid, startedUtc,
executableAbsolutePath, executableSha256, hostIp and hostPort. A container identity
contains kind=`container`, containerId, imageDigest, createdUtc, ownershipLabels,
hostIp, hostPort and containerPort. Its labels must bind the fixture lease.
Opposite-kind executable/container fields are rejected. A scanner database
identity contains engineVersion, loadedDatabaseVersion, databaseFilesSha256,
observedUtc and readinessReceiptSha256. Its observation must be no more than five
minutes old at admission; the hashes and readiness receipt identify the actual
loaded database evidence. Unknown admission configuration fields are rejected.

The File runtime validates the schema, compiled source/run binding, UTC lease,
endpoint binding and current evidence shape. **Copied JSON is not process,
container, socket or scanner-readiness proof.** Before issuing admission, the
launcher must independently inspect the exact File source/build, endpoint PID and
start/executable hash/listening socket or container creation/image/labels/mounts
and loopback ports. Reject foreign/persistent mounts. Observe the pinned real
ClamAV engine and loaded database files, retain a readiness receipt bound to this
run/attempt/source/resources, and execute benign, EICAR and unavailable controls.
Re-observe resources before and after completion/replay. No boolean ready field
replaces those checks. Expired admissions require new observation, not JSON lease
extension.

The actual SDK client uses explicit BaseUri, UnauthenticatedAccess and
EmulatorDetection.None. Every SDK request is checked against the exact scheme,
literal host, port and lease. Proxy use and automatic redirects are disabled;
redirects are rejected, and resumable Location values must stay at the same
origin. Status 308 is accepted only for PUT requests with byte Content-Range,
including zero-byte status probes; this is the GCS ResumeIncomplete protocol,
not redirect following. Actual SDK multi-chunk controls retain Range progression.
Connect timeout is five seconds. Response bodies retain lease checks before and
after every read and after serialization, with streaming consumption and finite
cancellation no later than the remaining lease or thirty seconds per operation.
Delayed metadata and final-upload bodies cannot produce accepted SDK results
after expiry. The ordinary credential acquisition
factory remains present but is not selected after admitted client registration;
failed admission has no ADC fallback.

The genuine SDK signer uses a fresh owned in-memory RSA key and a synthetic
`.invalid` identity. Its credential transport rejects all HTTP. Private keys are
never exported or persisted; DI disposal releases the RSA and SDK client.
Signing options explicitly retain V4 and the seven-day ceiling, generation and
content disposition, while applying the admitted origin before signing. URLs
are validated, never rewritten. SDK Options.Port is not cryptographically part
of the signature, so endpoint and transport admission separately enforce the
exact port.

Only this environment maps authenticated `GET /file/acceptance/signing-key`.
It returns Algorithm and the public SubjectPublicKeyInfo key for the launcher's
endpoint verifier, and returns 503 after lease expiry. It exposes no private
credential. Ordinary environments have no handshake route. The launcher must
bind the received public key to the independently admitted File process before
accepting signed object reads.

Legacy upload writes must be enabled; InstantQuoteFiles must remain disabled for
this bounded financial completion composition. The ordinary actual upload
application, PostgreSQL metadata, quarantine generation, copy/delete preconditions,
journals and read-authority checks remain in use. The scanner receives admitted
literal host/port with a ten-second timeout, preserves full INSTREAM scanning and
rejects use or clean promotion after lease expiry. Ordinary adapter and scanner
constructor signatures remain available.

The source tests distinguish controlled admission and real SDK offline signing,
transport isolation, and actual scanner protocol behavior from malware-engine or
eight-host financial acceptance. They do not claim an independently observed
storage endpoint, real database-loaded clamd or completed financial workflow.
Those hosted gates still require the reviewed launcher, compatible endpoint,
pinned daemon/database, exact bytes/generation/signature readback, real bearer and
PostgreSQL evidence, and verified finite cleanup. No launcher resource or cloud
activation is part of authoring this source seam.

The launcher owns all disposable endpoints, daemon/database, hosts and their
limits/leases. In finally, preserve evidence, stop exact owned identities
gracefully, recheck before force, verify exit/removal and fail acceptance on
cleanup uncertainty. Never remove persistent databases or shared resources.
