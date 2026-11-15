# Deploying the session adapter to Azure

The code is ready for local checks; existing Azure code is not automatically
updated. Do not enable Unity uploads until the deployed smoke test passes. Storage
and any scheduler can incur Azure charges; review your subscription's estimates.

## 1. Create a dedicated private storage account

In Azure Portal, create a Storage account under your permitted student region.
Use Standard, StorageV2, LRS for the initial prototype. Enable secure transfer and
TLS 1.2 or higher. Disable anonymous blob access. Do not use a storage account
containing unrelated data: the lifecycle below is intentionally short.

On this dedicated account's Data protection settings, turn off blob/container soft
delete, blob versioning and point-in-time restore; do not configure snapshots,
immutability, object replication or backup copies that retain sessions beyond the
agreed deadline. Portal-created accounts can have soft delete enabled by default.
The API checks service retention properties and refuses storage initialization
when soft delete/versioning/restore is enabled; it never changes these settings on
an existing account. External backups/replication still require operator review.

Create a private container `rhythm-sessions`, or let the server create it. Do not
enable static website hosting for this container, grant public access, expose a
SAS URL, or put its account key in the frontend. An optional starting template is
`deployment/session-storage.bicep`; it does not deploy the API or edit app settings.

## 2. Configure App Service secrets

App Service → Settings → Environment variables → App settings:

| Name | Value |
| --- | --- |
| `SessionApi__StorageProvider` | `AzureBlob` |
| `SessionApi__StorageConnectionString` | Connection string from the dedicated storage account's Access keys |
| `SessionApi__ContainerName` | `rhythm-sessions` |
| `SessionApi__MaintenanceKey` | A new independent 24-byte random value encoded as 32-character Base64url |
| `SessionApi__QrLoggingRedactionConfirmed` | Keep `false` until step 4 is complete |
| `GEMINI_API_KEY` | Existing server-only Gemini key; session transport does not use it |

Use the normal Production environment. Do not set `ASPNETCORE_ENVIRONMENT` to
Development in Azure. The Local storage provider is rejected outside Development;
there is no silent in-memory/temporary-disk fallback for Azure. A missing storage
configuration leaves the old root/coach routes available but returns a generic
503 on session operations and storage health checks.

The default browser allowlist is exactly `https://seaweedbrainn.github.io`.
If development origins are needed, use explicit `SessionApi__AllowedOrigins__0`,
`__1`, etc. Do not include a repository path or use wildcard origins. No cookies or
credentialed CORS are used. Avoid a separate incompatible App Service CORS policy;
the application answers preflight itself. Do not override framework request-log
levels to Information/Debug without redaction.

## 3. Publish and test the API

Publish the existing .NET 10 project using the existing Visual Studio profile, or
prepare a Release folder using `dotnet publish -c Release`. Verify publication
includes `Prompts/*` and `docs/session-api.openapi.json`.

Check the root, `/health` and `/openapi.json`. Health must return 200 before upload
is enabled. If storage health returns 503, check the connection string, networking,
private container and storage-retention settings. Logs deliberately omit upstream
exception bodies; inspect storage settings without enabling credential capture.

From PowerShell 7:

```powershell
.\tools\Test-SessionApi.ps1 -BaseUrl 'https://rhythm-backend-emmanuel-hvfkdjhxfjc4d9a5.westus-01.azurewebsites.net'
```

This creates a disposable session, commits song A, a replay and song B, checks
retry identity and all file downloads, checks scope isolation/preflight, then
revokes the session. It sends no Gemini requests. Never commit generated test keys.

## 4. Configure QR-link logging before enabling landing URLs

The QR contains a read capability in its query. Browser query stripping cannot
erase the first HTTP request at Azure's edge. Before enabling QR links:

- App Service → Monitoring → App Service logs: disable raw Web server logging,
  Detailed error messages and Failed request tracing unless their credential
  redaction has been demonstrated. Application logs can remain enabled at the
  checked-in levels; the code does not log session credentials or request bodies.
- Inspect Diagnostic settings for AppServiceHTTPLogs, Application Insights request
  telemetry, Azure Front Door/proxy logs and any other request collectors. Disable
  sensitive capture or apply verified redaction of `key`, Authorization and request
  bodies before storage/export. Do not enable third-party telemetry on the landing
  page. This project does not configure these account-level settings for you.
- Use disposable credentials to verify what every enabled logging sink retains.
  Do not test with a retained production QR key and assume redaction worked.

Only then set `SessionApi__QrLoggingRedactionConfirmed=true` and restart. Until
confirmed, production landing URLs return minimal 503 HTML and strip the query;
authenticated `/v1` transport can still be tested directly. Clients should keep
QR display/real-user use disabled until these release checks pass. The successful
landing never consumes the key or returns CSVs. Phone scanning should parse the
URL locally instead of opening it.

## 5. Schedule cleanup independently of App Service idling

An in-process worker runs every 15 minutes when the app is running. Free F1 can
unload; that worker alone cannot meet a deletion deadline while the app sleeps.
Configure an external scheduler (for example an Azure timer-triggered Function or
Automation runbook) to call this at least hourly, and alert on failures:

```http
POST https://<your-api-host>/internal/session-cleanup
Authorization: Bearer <SessionApi__MaintenanceKey>
```

The maintenance key is an operator secret, not a session upload key. It grants
cleanup only and must not be in WebGL or the phone client. The endpoint wakes the
app, processes expired/revoked sessions and orphan objects, and returns
`{"status":"completed"}`. Storage failures return 503. Retry on transient failures
and alert if a scheduled run does not finish; deletion deadlines cannot be promised
during an unmonitored outage. Large datasets may need a dedicated cleanup worker
rather than this prototype's bounded HTTP maintenance request.

`deployment/session-cleanup.workflow.yml.example` is an optional GitHub Actions
schedule template. GitHub schedules may be delayed or disabled and are not a hard
SLA. Use a monitored Azure scheduler if the 24-hour deletion requirement is strict.
Never implement cleanup by deleting every blob on a creation-age lifecycle rule:
that can remove active session data or anti-reuse tombstones and bypass revocation
semantics. Disable retention copies as described in step 1.

## 6. Joint release checks

Repeat document 19's joint checks against the actual HTTPS API and the deployed
GitHub Pages WebGL build. Verify one-scan, multi-attempt phone retrieval, missing
and cross-session keys on both files, read-key upload/revoke denial, retries and
conflicts, delayed uploads and pagination, 1/2-second data, all-miss and zero-offset
cases, autoplay/saturation metadata, and expiry/revocation after obtaining URLs.

Record the deployed commit/build version and schema URL. HTTP, emulator and fake
failure evidence must not be reported as verified glasses/hardware behavior.
Confirm Unity's miss-filter fix before accepting its claimed latency basis.

## Operational boundaries

- Use one app instance initially. Blob ETags support concurrent writers across
  instances, but in-process rate budgets restart with the app and multiply across
  instances. Add a distributed gateway limiter before scaling/untrusted launch.
- Proxy forwarding is not accepted blindly by application code. Verify the actual
  client-IP behavior in Azure; if all requests share a proxy IP, configure trusted
  forwarding before relying on per-IP budgets. The global registration budget and
  concurrency limiter still apply per instance.
- Anonymous registration plus quotas is not game attestation. Abuse controls and
  account-level storage/spend monitoring remain necessary for broad public use.
- Legacy coaching endpoints remain unauthenticated and consume your Gemini quota.
  Session upload credentials do not automatically protect them. Do not present this
  adapter as production-hardening those existing endpoints.
- The adapter returns stored CSVs. Gemini persistence, phone ingestion, TTS,
  conversation memory, and session-aware coaching are separate integrations.
