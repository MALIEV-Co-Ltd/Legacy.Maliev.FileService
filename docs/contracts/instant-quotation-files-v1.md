# Instant quotation files API v1

This Web-facing API creates an upload session, streams one CAD file at a time, and finalizes selected clean files for a quotation request. The FileService owns file intake and authoritative file-link state only. Web continues to own pricing and downstream quotation submission; this API has no price, amount, currency, or quotation-calculation fields.

All JSON property names are camelCase. Customer filenames are metadata only and never become storage object basenames. Storage credentials, browser/service credentials, private object names, stack traces, and dependency details are never returned. The `sessionToken` is an opaque, time-limited capability returned only when its session is created.

## Create a session

`POST /file/v1/instant-quotation/sessions`

Success: `201 Created`

```json
{
  "sessionId": "11111111-1111-1111-1111-111111111111",
  "sessionToken": "opaque-session-capability",
  "expiresAt": "2026-07-18T12:00:00+00:00"
}
```

Keep the token only for the active quote-upload workflow. It proves ownership for upload and finalization calls and cannot be recovered later.

## Upload one file

`POST /file/v1/instant-quotation/sessions/{sessionId}/files`

Required headers:

- `X-Quote-Session-Token`: the token returned when the session was created.
- `Idempotency-Key`: a caller-generated key unique to this upload operation.
- `X-Content-SHA256`: the hexadecimal SHA-256 digest of the exact file bytes.
- `Content-Type: multipart/form-data; boundary=...`

The multipart body must contain exactly one part named `files`. The file is streamed; the endpoint does not use `IFormFile` buffering. Zero parts, extra parts, a different field name, an empty file, or an invalid digest is rejected.

```http
Content-Disposition: form-data; name="files"; filename="customer-part.stl"
Content-Type: model/stl
```

Success: `201 Created`

```json
{
  "fileId": "22222222-2222-2222-2222-222222222222",
  "fileName": "customer-part.stl",
  "contentType": "model/stl",
  "sizeBytes": 123456,
  "sha256": "8f0d...64-hex-characters",
  "status": "clean"
}
```

The actual streamed bytes may not exceed 209,715,200 bytes (200 MiB). Supported extensions, matched case-insensitively, are exactly `.stl`, `.obj`, `.3mf`, `.step`, `.stp`, `.iges`, `.igs`, `.glb`, and `.gltf`.

## Finalize selected files

`POST /file/v1/instant-quotation/sessions/{sessionId}/finalizations`

Required headers are `X-Quote-Session-Token` and `Idempotency-Key`.

```json
{
  "quotationRequestId": "33333333-3333-3333-3333-333333333333",
  "fileIds": ["22222222-2222-2222-2222-222222222222"]
}
```

Success: `200 OK`

```json
{
  "quotationRequestId": "33333333-3333-3333-3333-333333333333",
  "files": [
    {
      "fileId": "22222222-2222-2222-2222-222222222222",
      "fileName": "customer-part.stl",
      "contentType": "model/stl",
      "sizeBytes": 123456,
      "sha256": "8f0d...64-hex-characters",
      "status": "finalized"
    }
  ]
}
```

Only file IDs owned by the same unexpired session and already recorded clean can be finalized. A file or token from another session is rejected without disclosing whether that resource exists.

## Errors

Errors use `application/problem+json` and RFC ProblemDetails with a stable `code` extension. Titles and details are safe public text.

| Status | `code` | Meaning |
|---:|---|---|
| 400 | `validation_error` | Headers, metadata, multipart shape, digest, extension, or selection is invalid. |
| 403 | `session_forbidden` | The session token cannot authorize the requested session. |
| 409 | `idempotency_conflict` | The same idempotency key was already bound to a different request fingerprint. |
| 413 | `payload_too_large` | Actual streamed file bytes exceed 200 MiB. |
| 503 | `dependency_unavailable` | Required storage, scanning, or durable state is temporarily unavailable. |
| 503 | `outcome_unknown` | A failure left an ambiguous result that requires identical replay. |

```json
{
  "type": "https://docs.maliev.com/problems/idempotency_conflict",
  "title": "Idempotency replay conflict",
  "status": 409,
  "detail": "The idempotency key is already associated with a different request.",
  "code": "idempotency_conflict"
}
```

## Ownership and retry rules

- Treat `sessionToken` and every idempotency key as secrets; do not put them in URLs or logs.
- Retry an interrupted upload or finalization with the same session, token, idempotency key, and identical request content.
- An identical completed retry returns the recorded result. Reusing a key with changed bytes, digest, metadata, quotation request, or file selection returns `idempotency_conflict`.
- For `outcome_unknown`, retry the identical request with the same idempotency key until the authoritative result can be reconciled.
- For `dependency_unavailable`, retry with backoff. Cancellation itself is propagated and is not rewritten as a successful or definitive failed outcome.
