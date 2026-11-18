# Rhythm Backend

The Rhythm Backend is the ASP.NET Core service connecting the Musical Spartans
rhythm game, Android companion app, GitHub Pages frontend, Azure Blob Storage, and
Gemini coaching.

## Project links

- [Devpost project](https://devpost.com/software/musical-spartans)
- [Android app](https://github.com/SeaweedBrainn/MusicalSpartans)
- [WebGL build repository](https://github.com/SeaweedBrainn/MusicalSpartans.github.io)
- [Live RhythmEngine WebGL app](https://seaweedbrainn.github.io/MusicalSpartans.github.io/)
- [Production API](https://rhythm-backend-emmanuel-hvfkdjhxfjc4d9a5.westus-01.azurewebsites.net)
- [API health](https://rhythm-backend-emmanuel-hvfkdjhxfjc4d9a5.westus-01.azurewebsites.net/health)
- [OpenAPI schema](https://rhythm-backend-emmanuel-hvfkdjhxfjc4d9a5.westus-01.azurewebsites.net/openapi.json)

## What it provides

- Gemini-powered rhythm coaching
- Session registration with separate read and upload capabilities
- Authenticated Unity uploads of `series.csv` and `summary.csv`
- Private Azure Blob Storage persistence
- QR-based phone retrieval
- Per-song coaching and cross-song session debriefs
- Health checks, OpenAPI documentation, expiry, revocation, and rate limits

## Architecture

```text
Unity RhythmEngine ──upload_key──► Azure App Service API
                                        │
                                        ├──► Private Azure Blob Storage
                                        └──► Gemini API

Android phone / GitHub Pages frontend ──session_key──► Azure App Service API
```

Unity uploads gameplay data with an `upload_key`. The phone and frontend use the
separate `session_key` contained in the QR code. Gemini is called by the backend;
API keys are never placed in client applications.

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

Never commit API keys, storage connection strings, maintenance keys, or publish
profile credentials.

## Gemini setup

1. Open [Google AI Studio](https://aistudio.google.com/).
2. Create an API key for the intended Google project.
3. Store it locally with User Secrets.
4. Store it in Azure App Service as `GEMINI_API_KEY` for production.

The coaching prompt and response schema are in:

- [`RhythmBackend/Prompts/coach-system-prompt.md`](RhythmBackend/Prompts/coach-system-prompt.md)
- [`RhythmBackend/Prompts/coach-response-schema.json`](RhythmBackend/Prompts/coach-response-schema.json)

## Azure deployment

Follow the [backend setup guide](RhythmBackend/BACKEND-SETUP.md) and the
[Azure deployment guide](RhythmBackend/docs/AZURE-DEPLOYMENT.md). The optional
storage template is [`RhythmBackend/deployment/session-storage.bicep`](RhythmBackend/deployment/session-storage.bicep).

Required production settings include:

```text
SessionApi__StorageProvider=AzureBlob
SessionApi__StorageConnectionString=<secret>
SessionApi__ContainerName=rhythm-sessions
SessionApi__MaintenanceKey=<secret>
GEMINI_API_KEY=<secret>
```

## API and CSV contract

Important routes:

```text
GET  /health
GET  /openapi.json
POST /v1/sessions
PUT  /v1/sessions/{session_id}/runs/{run_id}
GET  /v1/sessions/{session_id}/runs
POST /v1/sessions/{session_id}/runs/{run_id}/coach
POST /v1/sessions/{session_id}/coach
```

Read the complete [session API contract](RhythmBackend/docs/SESSION-API.md) and
[phone-app handoff](RhythmBackend/docs/PHONE-APP-HANDOFF.md).

The HTTP envelope remains `contract_version: 1`. Detailed per-note series data
uses CSV `format_version,2`; the summary CSV remains the eight-field headerless
format. Do not silently convert V2 data to V1 or treat blank values as zero.

## Testing

From the repository root:

```powershell
dotnet build --no-restore
dotnet run --project .\RhythmBackend\tests\CoachContractChecks\CoachContractChecks.csproj
pwsh .\RhythmBackend\tools\Test-SessionApi.ps1 -BaseUrl "https://YOUR-APP.azurewebsites.net"
```

The smoke test uses temporary credentials and does not call Gemini.

## Documentation

- [Setup guide](RhythmBackend/BACKEND-SETUP.md)
- [Architecture](RhythmBackend/docs/BACKEND-ARCHITECTURE.md)
- [LLM handoff](RhythmBackend/docs/LLM-HANDOFF.md)
- [Phone app handoff](RhythmBackend/docs/PHONE-APP-HANDOFF.md)
- [RhythmEngine handoff](RhythmBackend/docs/RHYTHM-ENGINE-HANDOFF.md)
- [Session API](RhythmBackend/docs/SESSION-API.md)
- [Azure deployment](RhythmBackend/docs/AZURE-DEPLOYMENT.md)
- [Test evidence](RhythmBackend/docs/TEST-EVIDENCE.md)
