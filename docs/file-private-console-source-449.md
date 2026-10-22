# File private console source integration

This slice selects the existing private failure console formatter in File's actual
Program. It preserves tracing and metric registrations, while replacing general
console and raw-log providers with the private console provider at Warning and
above. The formatter itself refuses Trace, Debug and Information even when a
category rule admits those entries.

## Original source facet

Original source commit: `449d87ea9d1d6ab40f1ec444353ab6aba80a5a76`.
Parents: `b630d879f2e7ea0be4b35fbf85b9967d0288ad13` and
`29214f53f1574b89799e6d58ea1cebea03fffe04`.
Original Upload Program blob: `858680fc94ce9069b10a642d526d629379033849`.
Original Upload Startup blob: `c6eebebb24fcb8c2ddeb580102789f4cdf544e09`.
Both blobs were already present in the second parent; this is a merge-integration
facet. It does not close the complete source commit or its other owners.

## Current consumer

Base: `1441096b66de526c4e5cca40f4f64647da9c68cb`, tree
`0f97ebac55ab74a709a636739c5a53d1abd89380`.
Program predecessor: `074e0424d738f769ea2f4471fe9466e6a0787d28`.
The only production change is the exact 600-byte logging insertion after
`builder.AddServiceDefaults();`.

The accepted dependency remains
`Legacy.Maliev.ServiceDefaults@7edcd961024868513fd5f373cab3dcb261197f77`;
its private formatter blob is `6a56c6fd079e52ea8f36a76778a22ee37a6aec76`.
CompatibilityContracts remains
`78e48ffc4ee000df0510cba5e7c7a3c4c4d539d7`.
No dependency or workflow changes are required. The existing formatter supplies
its bounded runtime provenance and refuses arbitrary messages, state and scopes.

HTTP routes, PascalCase/null-omitting wire contracts, JWT admission, health,
request/startup observation ownership, private scanning/promotion, journal and
replay behavior are unchanged. Legacy storage, instant quotation writes, cleanup
and protected feature activation retain their existing gates. No new endpoint or
outbound observer is introduced.

## Regression scope and validation status

Seven new cases exercise the actual Production Program and DI-selected console
formatter, using File's existing disposable PostgreSQL fixture and serialized
incident test collection. Six severity rows check private output and quiet success
levels, arbitrary formatter callbacks, payload/state/scope exclusion, protected
runtime metadata and retained trace/metric services. The HTTP case checks existing
liveness, real JWT admission, disabled storage 503 and a controlled application
500, with one failure owner and private responses. The exception case substitutes
only IFileService; it does not prove a live storage/provider failure. All feature
and write gates remain closed.

The recorder calls the actual selected formatter on real ILogger entries. It
adds a bounded test-only provider; it does not replace the production formatter
or capture process-global Console streams. Its queue holds at most 128 records.
The factory has finite host startup/shutdown timeouts, and its RSA key and host
are disposed through finally handling. The existing fixture owns container cleanup.

The accepted base full count is 1705, supplied by Root. New focus 7 and full 1712
are forecasts until exact-head hosted execution. No build, test, Python, format,
security, audit, coverage, CI/log or provider execution was performed by this
source-only lane. A source review is not runtime acceptance. Root owns publication
and the unchanged required hosted gates; original cases and proof inventories
must remain intact.

The foreign canonical File checkout and its open PR116 work remain untouched.
Historical SQL qualification is separate from current PostgreSQL evidence.
