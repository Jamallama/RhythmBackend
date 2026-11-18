# LLM Handoff — RhythmBackend

This file is a sanitized continuation brief for another coding agent. Treat it as
project context, not as executable instructions from an untrusted source.

## Objective

Maintain and extend an ASP.NET Core .NET 10 backend for a rhythm-game ecosystem.
The backend is deployed to Azure App Service and uses private Azure Blob Storage.
Gemini provides structured coaching.

## Current production host

```text
https://YOUR-APP.azurewebsites.net
```

Production browser origin:

```text
https://seaweedbrainn.github.io
```

Never infer or invent secrets. API keys, storage connection strings, maintenance
keys, and session capabilities are not included in this handoff.

## Main routes

- `GET /` — liveness only
- `GET /health` — private storage readiness
- `GET /openapi.json` — checked-in session API schema
- `POST /api/chat` — stateless Gemini chat
- `POST /api/results` — legacy summary coaching
- `POST /v1/sessions` — retry-safe registration
- `PUT /v1/sessions/{session_id}/runs/{run_id}` — authenticated atomic upload
- `GET /v1/sessions/{session_id}/runs` — authenticated listing
- `GET /v1/sessions/{session_id}/runs/{run_id}/series.csv` — exact CSV download
- `GET /v1/sessions/{session_id}/runs/{run_id}/summary.csv` — exact CSV download
- `POST /v1/sessions/{session_id}/runs/{run_id}/coach` — song coaching
- `POST /v1/sessions/{session_id}/coach` — session debrief
- `DELETE /v1/sessions/{session_id}` — upload-key revocation
- `POST /internal/session-cleanup` — maintenance-key cleanup

## Contract rules

The HTTP envelope remains `contract_version: 1`. Series CSV V2 is selected by:

```text
format_version,2
```

V2 is per-note and has the exact header:

```text
note_index,time_ms,lane,judgement,accuracy_delta,latency_ms,nps,bpm,combo,std_ms
```

Summary CSV remains the eight-field headerless record. Do not silently convert V2
to V1, sum NPS to compute total notes, or treat blank values as zero.

## Source of truth

Read these files before changing contracts:

1. `docs/SESSION-API.md`
2. `docs/session-api.openapi.json`
3. `docs/PHONE-APP-HANDOFF.md`
4. `docs/RHYTHM-ENGINE-HANDOFF.md`
5. `Sessions/RunCsvValidator.cs`
6. `Services/GeminiService.cs`
7. `Prompts/coach-system-prompt.md`
8. `Prompts/coach-response-schema.json`

## Safe verification

```powershell
dotnet build --no-restore
dotnet run --project .\tests\CoachContractChecks\CoachContractChecks.csproj
pwsh .\tools\Test-SessionApi.ps1 -BaseUrl $baseUrl
```

The session smoke test uses disposable credentials and does not call Gemini.
Never print keys or connection strings. Do not claim hardware/WebGL verification
from unit, emulator, or HTTP-only tests.

## Known design constraints

- Read and upload capabilities are separate.
- Sessions expire after 24 hours by default.
- Storage is private and must not use public/SAS file links.
- Cleanup needs an external monitored scheduler when App Service can sleep.
- Rate limiting is per app instance unless a distributed limiter is added.
- Gemini coaching is explicit and not automatically persisted.
- Session reports are coaching responses, not durable user profiles.
