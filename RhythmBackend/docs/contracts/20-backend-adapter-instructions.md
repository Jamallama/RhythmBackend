# Backend developer adapter instructions

Implement a session adapter at the deployer's Azure backend (`https://YOUR-APP.azurewebsites.net/`) so the game can upload attempts and the phone client can retrieve both CSVs after one session QR scan.

Read the [shared adapter contract](19-rhythm-session-adapter.md) first. **[assumed] Its API routes and lifecycle values are a proposal, not a description of confirmed deployed behavior.** [docs] The required session/attempt distinction and existing CSV formats come from the Owner and current engine source. Do not implement the disabled per-run Worker contract.

## Implement in this order

1. **Publish the endpoint mapping.** Inspect your existing service and map its routes to the shared contract. Publish `/openapi.json` or an equivalent checked-in API schema containing the actual payloads, status codes, and bearer scopes. Tell the engine developer the final API base and registration/upload paths. Keep `/sessions/{id}/?key=...` as the QR link shape.
2. **Add session records.** Store the app-generated session ID, read-key hash, independent upload-key hash, creation/expiry/deletion times, and revocation state. Registration must be atomic and safe to retry with both original keys. Reject attempts to overwrite a session with different keys. Open anonymous registration needs rate limits and storage quotas; a key embedded in a public WebGL build would not authenticate the game.
3. **Add completed run records.** Use `(session_id, run_id)` as a unique key. Store raw content identity (`game_id`, `song_id`, `chart_id`), client finish time, server publication time and sequence, sampling interval, autoplay, successful-hit count, latency basis, summary range flag, content digest, and private file references. IDs for a run, song, and chart are different fields.
4. **Accept both CSVs in one PUT.** Authenticate the upload key, validate the envelope and the two formats, and persist the exact decoded UTF-8 CSV content. Follow the shared contract's exceptional handling for saturated summary fields. Stage storage before publishing the completed record. Reads must require that record; partial uploads never appear as completed. Recheck session state at commit and handle simultaneous retries safely.
5. **Implement listing and both CSV routes.** Require the session read key for each. Return the specified sequence pagination and relative authenticated file paths. Do not return direct blob links. A valid read credential must be scoped to the exact session; a write credential must not accidentally pass the read middleware or vice versa.
6. **Implement expiry, revocation, and cleanup.** Confirm the proposed 24-hour lifetime and deletion within a further 24 hours with the Owner. Enforce denial immediately at expiry/revocation on every API request. Cleanup must remove both blobs and relevant database data, including abandoned upload objects, with consistent backup retention. Keep enough tombstone state for safe retries during the cleanup interval.
7. **Configure the deployed service.** Allow the actual Pages origin and required preflight methods/headers. Configure private storage access with server-held credentials or managed identity. Add request-size limits, rate limits, no-store responses, and key redaction in every logging layer before testing QR URLs.

## Authentication details that must remain separate

The game generates both secrets once and supplies them at registration. Only `session_key` appears in the QR. The game uses `upload_key` in the authorization header for uploads and revocation. The phone uses `session_key` in the authorization header for listing and both CSV downloads.

Do not issue upload permission merely because a caller knows the read key. Do not implement a static access key, public CSV directory, or GitHub repository write token. CORS allows the browser to call the API; it does not stop unauthorized HTTP clients.

Registration responses contain no credentials. Identical registration retries retain the original expiry; identical run retries retain the original sequence and completion time. Changed retry content returns `409` without overwriting files.

## CSV and coaching boundary

[docs] The series has five metadata rows before its header; the summary has one row and no header. Latency units are milliseconds, blank cells mean unavailable, and the current `nps` field contains a count per window. A two-second window must be divided by two to derive notes per second.

Require `latency_basis = successful_hits_only_v1` only after the engine fix is in place. The backend cannot remove timing misses from already aggregated means. Preserve `successful_hit_count` so zero successful hits does not become a false claim of perfect zero-ms timing. Preserve `autoplay` and `summary_exceeds_format_range` in listing responses for the phone's coaching adapter.

## What to return to the other developers

- Actual OpenAPI schema or route mapping, deployed version, and an unauthenticated health route if available.
- Confirmed API base, public session-link root, allowed Pages origin, expiry/deletion policy, limits, and error codes.
- Disposable successful registration, upload, listing, and CSV retrieval examples with credentials removed.
- Evidence for identical PUT retries, partial storage failure, wrong-session access, read-key write denial, revocation, and expiry.

Use the joint acceptance checks in the shared contract with the engine and phone developers. An API that accepts an upload but cannot authorize both downloads is incomplete.

```text
HANDOFF
from: Backend developer   to: Rhythm engine developer + phone developer   time: <local>
done: <deployed routes and checks, labelled as HTTP evidence rather than hardware verification>
state: <API version/schema location; public session-link root; approved configuration values>
next: <run a two-song WebGL session and retrieve both attempts from one scan>
blockers/risks: <unmapped routes, CORS, storage, expiry, or data-quality gaps>
```
