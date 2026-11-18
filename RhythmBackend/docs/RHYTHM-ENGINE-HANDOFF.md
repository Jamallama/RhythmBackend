# RhythmEngine integration handoff — session API v1

Prepared 2026-11-14 (America/Los_Angeles) for the Unity/RhythmEngine developer.

## Status and source of truth

The backend now implements document 19's session API in the local working tree.
Document 21's Unity responsibilities remain applicable. The previous proposal to
upload CSV files through a multipart coaching endpoint is superseded: **send one
JSON PUT containing both CSV strings for each attempt**.

**Not yet confirmed deployed.** The owner is currently configuring Azure Storage.
The existing Azure root response does not establish that the new routes are live.
Keep production uploads and real-user QR usage disabled until the backend owner
confirms deployment, storage health, external logging configuration and the deployed
smoke test. These documents do not assert a committed/pushed backend revision.

Read these files in order:

1. This handoff, for engine-specific integration details and release gates.
2. [SESSION-API.md](SESSION-API.md), the implemented route mapping and behavior.
3. [session-api.openapi.json](session-api.openapi.json), the request/response schema.
4. [Original document 21](contracts/21-rhythm-engine-adapter-instructions.md), the
   engine-side source references and test requirements.
5. [TEST-EVIDENCE.md](TEST-EVIDENCE.md), what has and has not been verified.

The original documents in `contracts/` are preserved verbatim. Their proposed
routes now have the implementation mapping above; their assumed Pages origin is
replaced by the confirmed origin below. Their engine source references were supplied
by the coordinator and have not been independently re-inspected by this backend work.

## Confirmed configuration

```text
ApiBaseUrl = https://YOUR-APP.azurewebsites.net
PublicClientUrl = <ApiBaseUrl>/sessions
Browser origin = https://seaweedbrainn.github.io
Contract version = 1
```

The production browser origin contains no repository path. Use HTTPS directly and
do not forward credentials across redirects. The public WebGL build needs neither
an Azure Storage connection string, a Gemini key, nor a shared developer secret.

## 1. Session identity and registration

Preserve the existing `SessionId` and `SessionKey`, plus an independent `UploadKey`,
for the app session. Scene changes, song changes and upload retries retain them.
Browser refresh/restart starts a new session; recovery across refresh is not included.

- Session and run IDs: exactly 32 lowercase hexadecimal characters.
- Each key: 24 cryptographically random bytes, encoded as 32 unpadded Base64url
  characters. Read/upload keys must be independent and cannot be equal.
- Retain keys only in game memory. Do not derive an upload key from the QR key or
  put secrets in CSVs, source code, PlayerPrefs, analytics or public config.

```http
POST /v1/sessions
Content-Type: application/json
```

```json
{
  "contract_version": 1,
  "session_id": "<session-id>",
  "session_key": "<read-key>",
  "upload_key": "<independent-upload-key>"
}
```

Treat only a valid 201 or 200 response identifying the expected session and
`contract_version: 1` as registration acknowledgement. The response also supplies
`created_at` and `expires_at`; it never echoes keys or constructs the QR URL.
Retry a lost response using the same ID and both original keys. A changed key
returns 409. Expiry is fixed at registration and does not slide on retries/uploads.

## 2. Freeze and upload each attempt

Reuse the engine's existing run ID. Every replay gets a new run ID; song/chart
identifiers are never attempt IDs. Queue completed attempts until registration is
acknowledged. Prefer serialized uploads initially and keep the coordinator alive
across scene changes, independent of the results screen's coroutine lifecycle.

```http
PUT /v1/sessions/<session-id>/runs/<run-id>
Authorization: Bearer <upload-key>
Content-Type: application/json
```

Complete valid data example, apart from IDs/keys in the URL/header:

```json
{
  "contract_version": 1,
  "finished_at_utc": "2026-11-15T04:03:42Z",
  "game_id": 2,
  "song_id": 103,
  "chart_id": 1,
  "sample_interval_sec": 1,
  "autoplay": false,
  "latency_basis": "successful_hits_only_v1",
  "successful_hit_count": 1,
  "summary_exceeds_format_range": false,
  "series_csv": "game_id,2\nsong_id,103\nchart_id,1\nbpm,120\nsample_interval_sec,1\ngame_time,percent_accuracy,avg_latency,bpm,nps,latency_spread\n0,,,120,0,\n1,100,-12,120,1,\n",
  "summary_csv": "02,103,3,2000,120,1,1000000,-12"
}
```

All properties are required, including false/zero values. Names are case-sensitive
snake_case. Unknown properties are rejected. Use a proper JSON serializer with
explicitly named DTO fields/properties; do not concatenate unescaped CSV text.

`finished_at_utc` must end in **Z**. A `DateTimeOffset.ToString("O")` commonly emits
`+00:00`, which the current API rejects. For a DateTimeOffset use:

```csharp
finishedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)
```

For a DateTime use `ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)`.
Compute the string once when freezing the attempt, not on each retry.

Freeze the entire payload, metadata and both original CSV strings. JSON property
order/escaping may change without changing content identity, but the decoded CSV
bytes must not change: LF/CRLF normalization or an added final newline is a conflict
when retrying an already committed attempt.

First commit returns 201; identical retries return 200 with the original receipt:

```json
{
  "session_id": "<session-id>",
  "run_id": "<run-id>",
  "sequence": 1,
  "status": "completed",
  "completed_at": "2026-11-15T04:03:44Z"
}
```

Verify expected IDs, positive sequence and completed status before marking uploaded.
The server sequence is publication order, not song order or client finish time.
The backend publishes only after both private CSV objects are stored. An upload
does not wait for Gemini and does not return coaching text.

## 3. Required timing/data behavior

Confirm the engine fix before sending `successful_hits_only_v1`:

- Include only non-Miss judgements with a non-null signed offset in latency means
  and spread. Keep misses in accuracy and note/window counts. Preserve miss offsets
  in note-level diagnostics, not in successful-hit aggregates.
- `successful_hit_count` uses that same population. It must not exceed the total
  window note count. Zero hits means unavailable series latency/spread; summary
  average latency remains its legacy numeric-zero sentinel, not real zero timing.
- One successful hit cannot produce a spread. A real zero-ms offset with positive
  hit count is valid. Negative means early, positive means late.
- Preserve five metadata rows, the exact series header, headerless summary, optional
  measurement blanks and the current CSV formatting. Summary accuracy is absent.
- Sampling interval can be 1 or 2. `nps` holds a count per window; do not rewrite it
  as a per-second rate. The server expects window starts 0, interval, 2×interval, etc.
- Preserve `autoplay` and `summary_exceeds_format_range`. Keep raw envelope IDs;
  clamped summary IDs can differ only as explained by the documented format limits.
  `chart_id` is distinct from the 1–4 summary difficulty band.
- Series BPM may be fractional; summary nominal BPM is rounded. Note-count mismatches
  not explained by documented saturation return 422. Do not try to repair aggregates
  client-side by inventing miss counts or averaging window means into a song mean.

## 4. Retry/error behavior

Errors have the shape `{"error":{"code":"...","message":"..."}}`.

| Outcome | Engine behavior |
| --- | --- |
| 200/201 with valid matching completed receipt | Mark the frozen attempt uploaded |
| Network failure, timeout or 5xx | Retry identical ID/payload using bounded backoff and jitter |
| 429 `rate_limited` | Respect `Retry-After`, then retry the same request |
| 429 `session_quota_exceeded` | Stop automatic retry; preserve local export and surface the quota failure |
| 400/401/403/409/410/413/422 | Surface failure; do not rotate session/key/run ID to conceal the error |

Limits are 256 KiB UTF-8 per CSV, 2 MiB JSON request and 200 attempts/session.
Sessions expire after 24 hours. Correctly authenticated expired/revoked requests
return 410 while session tombstone metadata remains; after physical metadata
cleanup they can return 401. Neither requires automatic credential rotation.

Temporary per-process rate budgets include five registrations/minute/IP and
100/hour globally. Do not register per song. Upload/registration concurrency is
bounded; serialized uploads reduce contention. The server operation deadline is
two minutes, so use a deliberate client timeout and retry policy. Local exports
must survive permanent upload failures during the running app session.

## 5. One session QR and phone retrieval

The game constructs the QR locally:

```text
<PublicClientUrl>/<SessionId>/?key=<SessionKey>
```

The URL includes `/sessions` exactly once, no run ID and no upload key. Both
results-screen QR choices keep this same session link. Never log the full QR URL.
Retire the old per-run POST/reference path or leave it unreferenced; do not issue
or expect server-generated per-run QR credentials.

The phone scans once and uses the retained read bearer on each request:

```text
GET /v1/sessions/{session_id}/runs?after_sequence=0&limit=100
GET /v1/sessions/{session_id}/runs/{run_id}/series.csv
GET /v1/sessions/{session_id}/runs/{run_id}/summary.csv
```

The listing supplies the relative CSV paths. New attempts appear under the same
read capability; an empty listing does not require another scan. A QR image or
configured URL alone is not proof that registration/upload succeeded.

The phone must keep its ingestion cursor and coached-run IDs separately. It should
advance ingestion only after both files/metadata are retained, and should not coach
autoplay runs as player performance. This phone behavior is outside the engine's
upload implementation and still requires joint integration tests.

## 6. Deployment gates and return handoff

The backend owner must supply deployment confirmation before live uploads:

1. The deployed build/commit identifier and schema matching this mapping.
2. 200 from `/health` and the deployed disposable-session smoke test passing.
3. Confirmed CORS for `https://seaweedbrainn.github.io`, external credential-safe
   logging, session cleanup scheduling and storage retention configuration.
4. Confirmation that QR links are enabled. The HTML landing intentionally returns
   503 until the operator confirms its logging configuration; the phone should
   parse the QR locally, not open it just to read the key.

The smoke test is `../tools/Test-SessionApi.ps1` relative to this document. It uses
PowerShell 7 and creates/revokes disposable sessions; it prints no keys. The backend
has 201 local/emulator contract checks and 25 existing coaching checks passing.
These are not proof of a deployed service, actual WebGL or glasses behavior.

Engine acceptance: run core/compile checks, timing-regression cases, lost-response
and scene-change retries, then rebuild WebGL. With the phone developer, scan once,
play A, replay A and play B; retrieve both CSVs for all three run IDs. Re-send one
committed PUT and confirm there are still three attempts, then change a CSV byte
and confirm 409 without changing the downloaded originals.

Return the engine revision/build URL, latency-fix/test results, frozen-payload retry
evidence, actual Pages-origin HTTP/preflight results, and the joint phone test
result. Label simulator/fake-service evidence as simulated. The broader CSV-to-facts
Gemini coaching and session debrief integration is separate from this transport API.
