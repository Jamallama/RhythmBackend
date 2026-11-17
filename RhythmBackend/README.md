# Rhythm Backend

The Rhythm Backend is the ASP.NET Core service connecting the Musical Spartans
rhythm game, Android companion app, GitHub Pages frontend, Azure Blob Storage, and
Gemini coaching.

## Project ecosystem

| Component | Link |
|---|---|
| Devpost project | [Musical Spartans on Devpost](https://devpost.com/software/musical-spartans) |
| Android companion app | [SeaweedBrainn/MusicalSpartans](https://github.com/SeaweedBrainn/MusicalSpartans) |
| WebGL repository | [SeaweedBrainn/MusicalSpartans.github.io](https://github.com/SeaweedBrainn/MusicalSpartans.github.io) |
| Live RhythmEngine WebGL app | [Play RhythmEngine](https://seaweedbrainn.github.io/MusicalSpartans.github.io/) |
| GitHub Pages origin | [seaweedbrainn.github.io](https://seaweedbrainn.github.io) |
| Production API | [Rhythm Backend](https://rhythm-backend-emmanuel-hvfkdjhxfjc4d9a5.westus-01.azurewebsites.net) |
| API health | [Health endpoint](https://rhythm-backend-emmanuel-hvfkdjhxfjc4d9a5.westus-01.azurewebsites.net/health) |
| API schema | [OpenAPI JSON](https://rhythm-backend-emmanuel-hvfkdjhxfjc4d9a5.westus-01.azurewebsites.net/openapi.json) |

The Unity/RhythmEngine source is maintained separately from this backend. The
public WebGL build is linked above; add the Unity source repository when it is
available publicly.

## What it provides

- Gemini-powered rhythm coaching
- Session registration with separate read and upload capabilities
- Authenticated Unity uploads of `series.csv` and `summary.csv`
- Private Azure Blob Storage persistence
- QR-based phone retrieval
- Attempt listing and exact CSV downloads
- Per-song Gemini coaching and cross-song session debriefs
- Health checks, OpenAPI documentation, rate limits, expiry, and revocation

## Architecture

```text
Unity RhythmEngine ──upload_key──► Azure App Service API
                                        │
                                        ├──► Private Azure Blob Storage
                                        └──► Gemini API

Android phone / GitHub Pages frontend ──session_key──► Azure App Service API
```

Unity uploads gameplay data with an `upload_key`. The phone and frontend use the
separate `session_key` contained in the session QR code. Gemini is called by the
backend; API keys are never placed in client applications.

## Production API

```text
https://rhythm-backend-emmanuel-hvfkdjhxfjc4d9a5.westus-01.azurewebsites.net
```

Important routes:

```text
GET  /health
GET  /openapi.json
POST /v1/sessions
PUT  /v1/sessions/{session_id}/runs/{run_id}
GET  /v1/sessions/{session_id}/runs
GET  /v1/sessions/{session_id}/runs/{run_id}/series.csv
GET  /v1/sessions/{session_id}/runs/{run_id}/summary.csv
POST /v1/sessions/{session_id}/runs/{run_id}/coach
POST /v1/sessions/{session_id}/coach
```

See [`docs/SESSION-API.md`](docs/SESSION-API.md) and
[`docs/session-api.openapi.json`](docs/session-api.openapi.json).

## Local development

Requirements: .NET 10 SDK, Visual Studio ASP.NET workload or `dotnet`, and
PowerShell 7 for smoke tests.

```powershell
dotnet restore
dotnet run
```

For local Gemini access, use User Secrets:

```powershell
dotnet user-secrets init
dotnet user-secrets set GEMINI_API_KEY "YOUR_GEMINI_API_KEY"
```

Never put API keys in `appsettings.json`, source code, Unity, Android, or frontend
configuration.

## Gemini setup

1. Open [Google AI Studio](https://aistudio.google.com/).
2. Create an API key for the intended Google project.
3. Store it locally with User Secrets.
4. Store it in Azure App Service as `GEMINI_API_KEY` for production.

The coaching instructions are in
[`Prompts/coach-system-prompt.md`](Prompts/coach-system-prompt.md); the response
schema is in [`Prompts/coach-response-schema.json`](Prompts/coach-response-schema.json).

## Azure deployment

Follow [`BACKEND-SETUP.md`](BACKEND-SETUP.md) and
[`docs/AZURE-DEPLOYMENT.md`](docs/AZURE-DEPLOYMENT.md). The optional infrastructure
template is [`deployment/session-storage.bicep`](deployment/session-storage.bicep).

Required production settings include:

```text
SessionApi__StorageProvider=AzureBlob
SessionApi__StorageConnectionString=<secret>
SessionApi__ContainerName=rhythm-sessions
SessionApi__MaintenanceKey=<secret>
GEMINI_API_KEY=<secret>
```

Do not commit these values. Keep QR logging confirmation disabled until all Azure
and proxy logging sinks have been reviewed for credential redaction.

## CSV contract

The HTTP upload envelope remains `contract_version: 1`. The detailed series format
is V2, selected by `format_version,2`, with one row per judged note. The summary
CSV remains the eight-field headerless format. See the session contract and
handoffs for exact validation rules.

## Testing

```powershell
dotnet build --no-restore
dotnet run --project .\tests\CoachContractChecks\CoachContractChecks.csproj
pwsh .\tools\Test-SessionApi.ps1 -BaseUrl "https://YOUR-APP.azurewebsites.net"
```

The smoke test uses temporary credentials and does not call Gemini. Do not print
or commit generated keys.

## Handoffs

- [Backend setup](BACKEND-SETUP.md)
- [Architecture](docs/BACKEND-ARCHITECTURE.md)
- [LLM continuation handoff](docs/LLM-HANDOFF.md)
- [Phone app handoff](docs/PHONE-APP-HANDOFF.md)
- [RhythmEngine handoff](docs/RHYTHM-ENGINE-HANDOFF.md)
- [Session API contract](docs/SESSION-API.md)
- [Deployment guide](docs/AZURE-DEPLOYMENT.md)
- [Test evidence](docs/TEST-EVIDENCE.md)

## Security boundaries

- Never expose Gemini or Azure storage credentials in clients.
- Never put `upload_key` in a QR code.
- Send capabilities only as HTTPS bearer headers on API routes.
- Do not expose public Blob URLs or SAS links.
- Anonymous session registration is not user authentication.
- Do not claim hardware/WebGL verification from local or HTTP-only tests.
