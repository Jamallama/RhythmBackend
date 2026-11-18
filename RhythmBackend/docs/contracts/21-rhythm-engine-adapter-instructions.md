# Rhythm engine developer adapter instructions

Connect the Unity engine to the Azure session API while retaining one session QR across song changes and retries. Upload each completed attempt's existing `series.csv` and `summary.csv` together.

Read the [shared adapter contract](19-rhythm-session-adapter.md) first. **[assumed] Its HTTP routes, extra envelope fields, and limits are proposed until mapped to the backend developer's actual API.** [docs] The source references below were inspected under `D:\ImmerseTheBayProject\MusicalSpartansRhythmEngine\RhythmGameEngine\Assets\RhythmEngine\`. Use this active copy; older copies also exist in this workspace.

## Configure the adapter

The proposed values are:

```text
ApiBaseUrl = https://YOUR-APP.azurewebsites.net
PublicClientUrl = <ApiBaseUrl>/sessions
Registration = POST <ApiBaseUrl>/v1/sessions
Upload = PUT <ApiBaseUrl>/v1/sessions/<session_id>/runs/<run_id>
```

Confirm these routes with the backend developer before enabling upload. `BackendConfig.cs` currently has empty URLs and stale per-run/static-folder comments. Replace those comments and ambiguous `UploadUrl` usage with a session API base and public link root. No keys belong in configuration, source control, PlayerPrefs, build variables embedded in WebGL, or analytics.

## Implement in this order

1. **Preserve session identity.** `Runtime/App/GameSession.cs` already creates `SessionId` and `SessionKey` at app initialization. Add a separate cryptographically random `UploadKey` using the same 24-byte/Base64url method. Generate it once alongside the other values; scene changes, replays, and upload retries keep all three unchanged. Restart/refresh creates a new session.
2. **Register asynchronously.** Add a session-aware adapter that POSTs the session ID, read key, upload key, and `contract_version: 1` using `UnityWebRequest`. Retain the exact registration values until acknowledged. Retry a lost response with the same values. Do not register again for every song or automatically rotate keys on an error.
3. **Fix latency aggregation before uploading coaching data.** In `Core/PerSecondAggregator.cs`, require `result.Judgement != Judgement.Miss` as well as a non-null offset before adding to the latency list. Keep timing-miss offsets in `PlayRecord` for diagnostics, and keep misses in the existing accuracy and density calculation. `Core/Scoring.cs` already excludes misses from song-level mean/spread. Count successful results consistently for the upload's `successful_hit_count`.
4. **Freeze one payload per completed attempt.** `GameSession.CompleteRun()` calls `RunResult.From()` once. After that result exists, enqueue its existing `RunId`, `SeriesCsv`, `SummaryCsv`, and immutable metadata. Use `FinishedAt.ToUniversalTime()` in round-trip UTC format. Include chart IDs, interval, autoplay, successful-hit count, `latency_basis: successful_hits_only_v1`, and `Summary.ExceedsFormatRange` as specified in the shared contract. Use a proper JSON serializer or the project's tested JSON escaping; do not concatenate unescaped CSV strings.
5. **Upload through a session-lifetime worker.** Use a persistent coordinator (for example a `DontDestroyOnLoad` object) rather than a coroutine owned by `ResultsScreen`. Queue runs while registration is pending. PUT both CSV strings to the path containing the retained session ID and existing attempt ID with `Authorization: Bearer <UploadKey>`. Serialize uploads initially to simplify ordering and retries. Network work must not block gameplay or scene changes.
6. **Retry without duplicating attempts.** Retain the frozen request until a valid `200` or `201` completed response identifies the expected session/run. Retry network failures, timeouts, `429`, and `5xx` with bounded exponential backoff and jitter; respect `Retry-After`. Keep the same run ID, finish timestamp, metadata, and CSV bytes. Stop and surface permanent credential, validation, expiry, conflict, or size errors. Do not mint a replacement ID to hide a conflict. Local CSV export remains available.
7. **Keep the QR session-scoped.** Build `<PublicClientUrl>/<SessionId>/?key=<SessionKey>` with proper URI escaping. Both QR choices remain that same URL. `ResultsScreen.Bind()` currently installs a session link through the old `BackendRunReference` type even before registration/upload; replace that misuse with a clearly session-scoped reference. QR generation never authorizes writes or chooses a particular song. A scan before upload completion may find an empty listing; the phone must retain the session and retry.
8. **Distinguish publication state.** Use the existing status label/error surface to distinguish pending registration/upload, uploaded, and failed. A configured URL or rendered QR does not prove either CSV is available remotely. Store adapter state outside the results screen so revisiting it reflects the same upload state. Confirm any new interaction or screen design with the Owner.
9. **Retire the old path deliberately.** `Runtime/App/BackendRunUploader.cs` and `Core/BackendRunEnvelope` implement a different per-run POST and response. Do not re-enable them as-is or expect server-issued per-run QR credentials. Replace or remove their usage and update misleading comments/tests, `BackendConfig.FolderUrl(string runId)`, and the obsolete `Backend/README.md` in your engine change. Keep the session folder contract as the storage model.

## Data details to preserve

[docs] Replaying the same chart creates another attempt folder. `RunId` is never a song or chart identifier. Do not rewrite the CSV schema or place credentials inside either CSV.

[docs] `CsvWriter` emits five metadata rows, the table header, then data. Sampling can be one or two seconds; `Nps` is a count for that window. The backend and phone need `sample_interval_sec`. `SongSummary.ToCsv()` emits one headerless row with raw milliseconds for duration and a 1–4 difficulty band.

[docs] A zero-hit summary currently emits latency `0`. Supply `successful_hit_count: 0`; the phone must interpret that summary latency as unavailable. A true zero-ms hit remains valid when the count is positive. For the fixed series, no successful hits means empty latency/spread, and one successful hit means empty spread. Do not recover a whole-song mean by averaging window means.

[docs] `SongSummary.ExceedsFormatRange` flags saturation. Preserve the flag so the backend/phone cannot treat clamped duration, IDs, note count, or difficulty as exact facts. Preserve autoplay metadata so bot runs are never coached as player performance.

## Validate before handing off

- Run the existing Core tests and Unity compile check used by this project. Add focused regression cases for signed timing misses mixed with successful hits, all-miss windows, one-hit spread, and two-second windows. For example, one Perfect at -12 ms plus a timing Miss at +170 ms must yield mean -12 ms and empty spread; accuracy still reflects both notes.
- Exercise adapter retry behavior against a fake or disposable HTTP service: lost registration response, lost upload response, scene change during upload, duplicate callbacks, invalid credentials, and changed-content conflicts. Report fake-service evidence as [simulated].
- Rebuild and redeploy WebGL to the real Pages origin. Confirm browser preflight and authenticated requests there; editor-only success does not establish WebGL compatibility.
- Complete the shared contract's two-song and replay checks with the backend and phone developers. One scan must retrieve both files for multiple run IDs in the same session without further QR scans.

[assumed] Pending uploads are held in memory for this adapter. Browser refresh loses pending data and rotates the session. Durable cross-refresh upload recovery is additional scope; do not imply it exists.

```text
HANDOFF
from: Rhythm engine developer   to: Backend developer + phone developer   time: <local>
done: <adapter changes, latency fix, test and WebGL results with appropriate evidence labels>
state: <engine revision/build URL; confirmed API routes; pending/failed upload behavior>
next: <scan once, play/replay two songs, verify listing and both CSVs per attempt>
blockers/risks: <route mismatches, CORS, credentials, data quality, refresh data loss>
```
