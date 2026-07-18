# Instant quotation files API v1

This Web-facing API creates an upload session, streams one CAD file at a time, and finalizes selected clean files for a quotation request. FileService returns file and link authority only. GeometryService and the quotation backend own authoritative geometry/DFM analysis and quotation decisions; FileService never calculates geometry, DFM, or price. This API has no price, amount, currency, or quotation-calculation fields.

All JSON property names are camelCase. Customer filenames are metadata only and never become storage object basenames. Temporary private object names, storage credentials, browser/service credentials, stack traces, and dependency details are never returned. The finalized bucket and objectName are returned as the durable file authority for the downstream quotation workflow. The `sessionToken` is an opaque, time-limited capability returned only when its session is created.

## Create a session

`POST /file/v1/instant-quotation/sessions`

Success: `201 Created`

```json
{
  "sessionId": "11111111-1111-1111-1111-111111111111",
  "sessionToken": "opaque-session-capability",
  "expiresAt": "2026-07-18T12:00:00+00:00",
  "maxUploadBytes": 209715200,
  "supportedExtensions": [".stl", ".obj", ".3mf", ".step", ".stp", ".iges", ".igs", ".glb", ".gltf"]
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
      "bucket": "private-upload-bucket",
      "objectName": "instant-quotation/33333333333333333333333333333333/22222222222222222222222222222222.stl",
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

## Remove a pre-finalization file

`DELETE /file/v1/instant-quotation/sessions/{sessionId}/files/{fileId}`

Required header: `X-Quote-Session-Token`.

Success: `204 No Content`. Removal is idempotent: retrying an already removed file returns 204 without another storage operation. Clean, failed, or unknown temporary objects are conditionally deleted by their exact generation when one is recorded. Pending or uploaded work returns `upload_in_progress`; a finalized file is protected because it is already linked to a quotation request.

## Errors

Errors use `application/problem+json` and RFC ProblemDetails with a stable `code` extension. Titles and details are safe public text.

The 401 response is produced by platform authentication middleware and is not guaranteed to use this API's ProblemDetails body or stable `code`. The application-level ProblemDetails examples below therefore begin at 400 and cover every stable code emitted by this API.

| Status | `code` | Meaning |
|---:|---|---|
| 400 | `validation_error` | Headers, filename metadata, multipart shape, digest syntax, or selection is invalid. |
| 401 | platform authentication challenge | The caller has no accepted authenticated platform identity. |
| 403 | `session_forbidden` | The session token cannot authorize the requested session. |
| 409 | `idempotency_conflict` | The same idempotency key was already bound to a different request fingerprint. |
| 409 | `upload_in_progress` | An identical upload or finalization reservation is still pending. |
| 413 | `payload_too_large` | Actual streamed file bytes exceed 200 MiB. |
| 415 | `unsupported_media_type` | The declared media type is invalid, unsupported, or mismatched with the extension. |
| 422 | `unsafe_content` | The file signature, digest, or malware scan made the upload unsafe to accept. |
| 503 | `dependency_unavailable` | Required storage, scanning, or durable state is temporarily unavailable. |
| 503 | `outcome_unknown` | A failure left an ambiguous result that requires identical replay. |

```json
[
  { "status": 400, "code": "validation_error" },
  { "status": 403, "code": "session_forbidden" },
  { "status": 409, "code": "idempotency_conflict" },
  { "status": 409, "code": "upload_in_progress" },
  { "status": 413, "code": "payload_too_large" },
  { "status": 415, "code": "unsupported_media_type" },
  { "status": 422, "code": "unsafe_content" },
  { "status": 503, "code": "dependency_unavailable" },
  { "status": 503, "code": "outcome_unknown" }
]
```

## Ownership and retry rules

- Treat `sessionToken` and every idempotency key as secrets; do not put them in URLs or logs.
- Retry an interrupted upload or finalization with the same session, token, idempotency key, and identical request content.
- An identical completed retry returns the recorded result. Reusing a key with changed bytes, digest, metadata, quotation request, or file selection returns `idempotency_conflict`.
- For `outcome_unknown`, retry the identical request with the same idempotency key until the authoritative result can be reconciled.
- For `dependency_unavailable`, retry with backoff. Cancellation is driven by the HTTP request abort token, remains a request abort, and is not rewritten as a successful or definitive failed outcome.
