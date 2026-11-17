# Rhythm Backend Setup Guide

This guide explains how to run the ASP.NET backend locally and deploy it to Azure
App Service with private Azure Blob Storage and Gemini coaching.

## What the backend does

The backend provides:

- Gemini chat and song coaching
- Session registration with separate read and upload capabilities
- Authenticated Unity uploads containing `series.csv` and `summary.csv`
- Private Azure Blob Storage for session data
- QR-based phone retrieval
- Per-song and session-level coaching requests
- Health checks, OpenAPI documentation, rate limits, expiry, and revocation

## Prerequisites

- Visual Studio with the ASP.NET/.NET workload, or .NET 10 SDK
- PowerShell 7 for the smoke-test script
- An Azure subscription
- A Google AI Studio account for Gemini
- A dedicated Azure Storage account

## Run locally

From the project directory:

```powershell
dotnet restore
dotnet run
```

The development URLs are shown by ASP.NET, commonly:

```text
https://localhost:7033
http://localhost:5248
```

For local Gemini, store the key in User Secrets rather than `appsettings.json`:

```powershell
dotnet user-secrets init
dotnet user-secrets set GEMINI_API_KEY "YOUR_GEMINI_KEY"
```

Development storage defaults to the local session store. Never commit User Secrets,
API keys, connection strings, or publish-profile credentials.

## Obtain a Gemini API key

1. Open Google AI Studio: https://aistudio.google.com/
2. Sign in with the account that should own the key.
3. Open the API-key area and create a key for the intended Google project.
4. Copy it once and store it in a password manager or secret store.
5. For local work, use User Secrets.
6. For Azure, add it only as the App Service setting `GEMINI_API_KEY`.

The key belongs on the server only. Do not place it in Unity, Android, GitHub
Pages, JavaScript, APK resources, or committed configuration.

## Create Azure resources

Create a resource group and a dedicated StorageV2 account in a region permitted by
the subscription. The prototype configuration is:

- Standard performance
- LRS redundancy
- Hot access tier
- Secure transfer enabled
- TLS 1.2 or higher
- Anonymous blob access disabled
- Public container access disabled
- Blob soft delete disabled
- Container soft delete disabled
- Blob versioning disabled
- Point-in-time restore disabled

The optional template is `deployment/session-storage.bicep`. It creates the
storage account and private `rhythm-sessions` container; it does not deploy the
web app or print a connection string.

## Configure Azure App Service

In **App Service → Settings → Environment variables → App settings**, add:

| Name | Value |
|---|---|
| `SessionApi__StorageProvider` | `AzureBlob` |
| `SessionApi__StorageConnectionString` | Storage account connection string |
| `SessionApi__ContainerName` | `rhythm-sessions` |
| `SessionApi__MaintenanceKey` | Independent 32-character Base64url secret |
| `SessionApi__QrLoggingRedactionConfirmed` | `false` until logging review |
| `GEMINI_API_KEY` | Google AI Studio key |

Use the Production environment. Do not set Azure to Development or use the local
storage provider in production. Save/apply the settings and restart the app.

## Publish

In Visual Studio:

1. Right-click the `RhythmBackend` project.
2. Select **Publish**.
3. Choose the existing Azure App Service profile.
4. Confirm the target app and publish.

The published output must include `Prompts/*` and `docs/session-api.openapi.json`.

## Verify deployment

```powershell
$baseUrl = "https://YOUR-APP.azurewebsites.net"
Invoke-RestMethod "$baseUrl/"
Invoke-RestMethod "$baseUrl/health"
Invoke-RestMethod "$baseUrl/openapi.json"
```

Then run the disposable session test:

```powershell
pwsh .\tools\Test-SessionApi.ps1 -BaseUrl $baseUrl
```

The smoke test does not call Gemini. Test Gemini separately through `/api/chat` or
the authenticated coaching endpoints.

## QR logging gate

The QR URL contains a read capability in its query string. Review every enabled
Azure/proxy/Application Insights logging sink for query-string and authorization
redaction before setting:

```text
SessionApi__QrLoggingRedactionConfirmed=true
```

Until then, authenticated `/v1` API access works, but the QR landing page remains
disabled.

## Important files

| File | Purpose |
|---|---|
| `Program.cs` | ASP.NET startup and legacy coaching routes |
| `Services/GeminiService.cs` | Gemini calls and response validation |
| `Sessions/SessionEndpoints.cs` | Session HTTP routes |
| `Sessions/SessionService.cs` | Identity, authorization, lifecycle, cleanup |
| `Sessions/RunCsvValidator.cs` | V1/V2 CSV validation |
| `Sessions/AzureSessionObjectStore.cs` | Private Azure Blob persistence |
| `Prompts/coach-system-prompt.md` | Gemini coaching instructions |
| `Prompts/coach-response-schema.json` | Google GenAI response schema |
| `docs/SESSION-API.md` | Human-readable API contract |
| `docs/session-api.openapi.json` | Machine-readable API contract |
