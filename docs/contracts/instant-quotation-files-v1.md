# Instant quotation files API v1

This Web-facing API creates an upload session, streams one CAD file at a time, and finalizes selected clean files for a quotation request. FileService returns file and link authority only. GeometryService and the quotation backend own authoritative geometry/DFM analysis and quotation decisions; FileService never calculates geometry, DFM, or price. This API has no price, amount, currency, or quotation-calculation fields.

All JSON property names are camelCase. Customer filenames are metadata only and never become storage object basenames. Temporary private object names, storage credentials, browser/service credentials, stack traces, and dependency details are never returned. The finalized bucket and objectName coordinates are returned only on this authenticated FileService-to-BFF boundary so the BFF can create the existing QuotationRequest file link; the BFF must never return those coordinates to a browser or public client. The `sessionToken` is an opaque, time-limited capability returned only when its session is created.

## Authentication and browser boundary

Browsers do not call FileService and never receive a FileService JWT, service credential, Google credential, bucket, or object name. The browser calls the same-origin Web BFF using the Web application's anonymous or member session and normal request-forgery protections.

- For both anonymous and member workflows, the BFF calls FileService with the Web service's short-lived platform JWT and `legacy-file.uploads.create` permission. The current Web integration does not delegate the member subject to FileService.
- FileService binds the upload session to that Web service identity and the opaque quote-session capability. The BFF separately retains and binds the capability in server-side session state for the correct anonymous or member Web session.
- The BFF proxies upload, removal, and finalization. It binds the FileService session to the same Web quote session and does not place `X-Quote-Session-Token` in browser storage or URLs.
- Google Cloud Storage uses ADC/Workload Identity only inside FileService. Neither Web nor a browser supplies Google credentials.

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

The BFF keeps the token only for the active quote-upload workflow. It proves ownership for upload, removal, and finalization calls and cannot be recovered later.

## Upload one file

`POST /file/v1/instant-quotation/sessions/{sessionId}/files`

Required headers:

- `X-Quote-Session-Token`: the token returned when the session was created.
- `Idempotency-Key`: a caller-generated key unique to this upload operation.
- `X-Content-SHA256`: the hexadecimal SHA-256 digest of the exact file bytes.
- `Content-Type: multipart/form-data; boundary=...`

The multipart body must contain exactly one part named `files`. The file is streamed; the endpoint does not use `IFormFile` buffering. Zero parts, extra parts, a different field name, an empty file, or an invalid digest is rejected.

This is the normative multipart example represented by generated OpenAPI. The example file bytes are exactly `solid example\nendsolid example\n` in UTF-8, whose SHA-256 is shown below; production callers send the exact selected file bytes and their matching digest.

```http
POST /file/v1/instant-quotation/sessions/11111111-1111-1111-1111-111111111111/files HTTP/1.1
Authorization: Bearer <web-service-token>
X-Quote-Session-Token: <opaque-session-capability>
Idempotency-Key: upload-2222222222222222
X-Content-SHA256: dd75bf848e9a50028634377f2fad2b571fdd40b0461ee62359a95e27bbc62498
Content-Type: multipart/form-data; boundary=quote-boundary

--quote-boundary
Content-Disposition: form-data; name="files"; filename="customer-part.stl"
Content-Type: model/stl

solid example
endsolid example
--quote-boundary--
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

The declared part media type must match this matrix. `application/octet-stream` is also accepted for every listed extension because browsers do not consistently identify CAD formats.

| Extension | Accepted declared media types |
|---|---|
| `.stl` | `model/stl`, `application/sla`, `application/vnd.ms-pki.stl`, `application/octet-stream` |
| `.obj` | `model/obj`, `text/plain`, `application/x-tgif`, `application/octet-stream` |
| `.3mf` | `application/vnd.ms-package.3dmanufacturing-3dmodel+xml`, `application/octet-stream` |
| `.step`, `.stp` | `model/step`, `application/step`, `application/octet-stream` |
| `.iges`, `.igs` | `model/iges`, `application/iges`, `application/octet-stream` |
| `.glb` | `model/gltf-binary`, `application/octet-stream` |
| `.gltf` | `model/gltf+json`, `application/octet-stream` |

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

`bucket` and `objectName` are server-only linking coordinates for the BFF's existing QuotationRequest integration. They are not download URLs or public object identifiers and must be removed from any browser-facing response. Replacing these coordinates with an opaque cross-service link requires a coordinated QuotationRequest API change and is outside this compatibility contract.

## Remove a pre-finalization file

`DELETE /file/v1/instant-quotation/sessions/{sessionId}/files/{fileId}`

Required header: `X-Quote-Session-Token`.

Success: `204 No Content`. Removal is idempotent: retrying an already removed file returns 204 without another storage operation.

| Recorded file state | Removal result |
|---|---|
| `clean`, `failed`, `unknown` | Conditionally delete the temporary object by its exact recorded generation, then record `removed`; return 204. |
| `removed` | Perform no storage operation; return 204. |
| `pending`, `uploaded` | Return 409 `upload_in_progress`; retry after the in-flight operation resolves. |
| `finalized` | Return 403 `session_forbidden`; never delete a file already linked to a quotation request. |

An HTTP request abort cancels the in-flight operation through the request cancellation token. It is not converted to a 2xx response or a definitive failed state. If the durable outcome is ambiguous, retry the identical operation using its original idempotency key where that route requires one.

Removal can return 503 `outcome_unknown` when a conditional object deletion or durable state write has an ambiguous outcome. Because DELETE has no idempotency-key header, retry the same session/file DELETE; its recorded `removed` state makes the successful replay return 204 without another object deletion.

## Errors

Errors use `application/problem+json` and RFC ProblemDetails with a stable `code` extension. Titles and details are safe public text.

| Status | `code` | Meaning |
|---:|---|---|
| 400 | `validation_error` | Headers, filename metadata, multipart shape, digest syntax, or selection is invalid. |
| 401 | `platform_authentication_required` | The caller has no accepted authenticated platform identity. |
| 403 | `permission_forbidden` | The authenticated platform identity lacks `legacy-file.uploads.create`. |
| 403 | `session_forbidden` | The session token cannot authorize the requested session. |
| 409 | `idempotency_conflict` | The same idempotency key was already bound to a different request fingerprint. |
| 409 | `upload_in_progress` | An identical upload or finalization reservation is still pending. |
| 413 | `payload_too_large` | Actual streamed file bytes exceed 200 MiB. |
| 415 | `unsupported_media_type` | The declared media type is invalid, unsupported, or mismatched with the extension. |
| 422 | `unsafe_content` | The file signature, digest, or malware scan made the upload unsafe to accept. |
| 503 | `dependency_unavailable` | Required storage, scanning, or durable state is temporarily unavailable. |
| 503 | `outcome_unknown` | A failure left an ambiguous result that requires identical replay. |

Every problem has this exact shape; generated OpenAPI publishes a named example for every code reachable by each operation.

```json
{
  "type": "https://docs.maliev.com/problems/idempotency_conflict",
  "title": "Idempotency replay conflict",
  "status": 409,
  "detail": "The idempotency key is already associated with a different request.",
  "code": "idempotency_conflict"
}
```

Authentication also produces `WWW-Authenticate: Bearer`. Permission denial uses `permission_forbidden`; a valid platform caller that cannot prove ownership of a particular session receives `session_forbidden` instead.

## Ownership and retry rules

- Treat `sessionToken` and every idempotency key as secrets; keep the session token at the BFF and do not put either value in URLs or logs.
- Retry an interrupted upload or finalization with the same session, token, idempotency key, and identical request content.
- An identical completed retry returns the recorded result. Reusing a key with changed bytes, digest, metadata, quotation request, or file selection returns `idempotency_conflict`.
- For `outcome_unknown`, retry the identical request with the same idempotency key until the authoritative result can be reconciled.
- For `dependency_unavailable`, retry with backoff. Cancellation is driven by the HTTP request abort token, remains a request abort, and is not rewritten as a successful or definitive failed outcome.
