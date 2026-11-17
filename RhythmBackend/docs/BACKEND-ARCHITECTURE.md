# Rhythm Backend Architecture

```text
Unity RhythmEngine
   │ registration and upload_key-protected CSV uploads
   ▼
Azure App Service (ASP.NET Core)
   ├── Azure Blob Storage: private sessions, manifests, and CSVs
   ├── Gemini API: coaching requests only
   └── HTTPS API: phone/frontend reads and coaching
        ▲
        │ session_key
Android phone / GitHub Pages frontend
```

## Project links

- Devpost: https://devpost.com/software/musical-spartans
- Android app: https://github.com/SeaweedBrainn/MusicalSpartans
- WebGL build repository: https://github.com/SeaweedBrainn/MusicalSpartans.github.io
- Live RhythmEngine: https://seaweedbrainn.github.io/MusicalSpartans.github.io/

## Capabilities

Each session has an independent read key and upload key. The read key is placed in
the QR URL; the upload key remains with Unity. Keys are hashed before persistence.

## Session flow

1. Unity generates a session ID, read key, and upload key.
2. Unity registers them with `POST /v1/sessions`.
3. Unity uploads both CSVs atomically for each run.
4. The backend validates and publishes the run manifest.
5. The phone scans one QR and lists all completed runs.
6. The phone downloads exact CSV bytes.
7. The phone requests per-run or session coaching.

## Storage behavior

Azure Blob Storage is private. A manifest is the publication and revocation fence.
Staged files are not listed until both files and metadata are committed. Expiry and
revocation deny access immediately; cleanup removes payloads later.

## Coaching behavior

`/api/chat` is stateless general chat. Session coaching is explicit:

```text
POST /v1/sessions/{id}/runs/{run}/coach   song debrief
POST /v1/sessions/{id}/coach              session debrief
```

The backend sends validated facts/CSV data to Gemini and returns structured JSON.
Coaching responses are not persisted automatically.

## Boundaries

- The backend does not run Unity or scan QR codes.
- The backend does not expose Blob URLs or storage credentials.
- The backend does not repair missing measurements.
- Anonymous sessions are not user accounts.
- Gemini keys are server-side only.
