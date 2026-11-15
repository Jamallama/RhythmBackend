using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RhythmBackend.Sessions;

public sealed class SessionService(ISessionObjectStore store, SessionApiOptions options, TimeProvider clock)
{
    private static readonly Regex IdPattern = new("\\A[0-9a-f]{32}\\z", RegexOptions.CultureInvariant);
    private static readonly Regex KeyPattern = new("\\A[A-Za-z0-9_-]{32}\\z", RegexOptions.CultureInvariant);
    private static string StateName(string id) => $"states/{id}.json";
    private static string RetiredName(string id) => "retired/" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
    private static byte[] Encode<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, SessionJson.Options);
    private static SessionManifest Decode(StoredObject value) => JsonSerializer.Deserialize<SessionManifest>(value.Bytes, SessionJson.Options)
        ?? throw new InvalidOperationException("Unreadable session manifest.");
    public static bool ValidId(string? id) => id is { Length: 32 } && IdPattern.IsMatch(id);
    public static bool ValidKey(string? key) => key is { Length: 32 } && KeyPattern.IsMatch(key);

    public static void RequireId(string? id)
    {
        if (!ValidId(id)) throw new SessionApiException(400, "invalid_id", "Session and run IDs must be 32 lowercase hexadecimal characters.");
    }

    private static string Hash(string id, string scope, string key)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"rhythm-v1\0{id}\0{scope}\0{key}")));

    private static bool EqualHash(string left, string right)
        => CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));

    private void RequireLive(SessionManifest state)
    {
        if (state.RevokedAt is not null || state.Purged || clock.GetUtcNow() >= state.ExpiresAt)
            throw new SessionApiException(410, "session_gone", "Session has expired or been revoked.");
    }

    private void Authorize(SessionManifest state, string? key, bool upload)
    {
        if (!ValidKey(key)) Unauthorized();
        bool readMatch = EqualHash(state.ReadKeyHash, Hash(state.SessionId, "read", key!));
        bool uploadMatch = EqualHash(state.UploadKeyHash, Hash(state.SessionId, "upload", key!));
        if (!(upload ? uploadMatch : readMatch))
        {
            if (upload ? readMatch : uploadMatch)
                throw new SessionApiException(403, "wrong_scope", "This credential does not grant the required permission.");
            Unauthorized();
        }
        RequireLive(state);
    }

    public async Task<(SessionManifest State, bool Created)> RegisterAsync(RegisterSessionRequest request, CancellationToken ct)
    {
        RequireId(request.SessionId);
        if (request.ContractVersion != 1)
            throw new SessionApiException(422, "unsupported_contract_version", "Only contract_version 1 is supported.");
        if (!ValidKey(request.SessionKey) || !ValidKey(request.UploadKey) || request.SessionKey == request.UploadKey)
            throw new SessionApiException(400, "invalid_keys", "Provide independent 32-character Base64url read and upload keys.");
        string readHash = Hash(request.SessionId, "read", request.SessionKey);
        string uploadHash = Hash(request.SessionId, "upload", request.UploadKey);

        for (int attempt = 0; attempt < 32; attempt++)
        {
            // Non-reversible ID-only tombstones prevent a deleted session ID from
            // being revived without retaining keys, CSVs, or personal metadata.
            if (await store.ReadAsync(RetiredName(request.SessionId), ct) is not null)
                throw Conflict("session_conflict", "Session ID has already been used.");
            var existing = await store.ReadAsync(StateName(request.SessionId), ct);
            if (existing is not null)
            {
                var state = Decode(existing);
                bool readMatches = EqualHash(state.ReadKeyHash, readHash);
                bool uploadMatches = EqualHash(state.UploadKeyHash, uploadHash);
                if (!readMatches || !uploadMatches)
                    throw Conflict("session_conflict", "Session already exists with different credentials.");
                RequireLive(state);
                return (state, false);
            }
            var now = clock.GetUtcNow();
            var created = new SessionManifest(request.SessionId, readHash, uploadHash,
                now, now.AddHours(options.LifetimeHours), null, false, []);
            if (await store.WriteAsync(StateName(request.SessionId), Encode(created), null, ct)) return (created, true);
        }
        throw Busy();
    }

    public async Task<SessionManifest> ReadAsync(string id, string? key, bool upload, CancellationToken ct)
    {
        RequireId(id);
        var stored = await store.ReadAsync(StateName(id), ct);
        if (stored is null) Unauthorized();
        var state = Decode(stored!);
        Authorize(state, key, upload);
        return state;
    }

    public async Task<(RunManifest Run, bool Created)> UploadAsync(string id, string runId, string? key, UploadRunRequest request, CancellationToken ct)
    {
        RequireId(runId);
        var initial = await ReadAsync(id, key, upload: true, ct);
        RunCsvValidator.Validate(request);
        string digest = RunCsvValidator.Digest(request);
        var previous = initial.Runs.Find(run => run.RunId == runId);
        if (previous is not null) return Identical(previous, digest);
        if (initial.Runs.Count >= options.MaxRunsPerSession) throw Quota();

        // Every contender owns its staging prefix, so failed/concurrent losers can
        // never delete a winning request's objects. Only the manifest exposes data.
        string prefix = $"data/{id}/{runId}/{Guid.NewGuid():N}/";
        bool commitAttempted = false;
        try
        {
            if (!await store.WriteAsync(prefix + "series.csv", Encoding.UTF8.GetBytes(request.SeriesCsv), null, ct)
                || !await store.WriteAsync(prefix + "summary.csv", Encoding.UTF8.GetBytes(request.SummaryCsv), null, ct))
                throw new IOException("Could not stage attempt files.");

            for (int attempt = 0; attempt < 32; attempt++)
            {
                var stored = await store.ReadAsync(StateName(id), ct);
                if (stored is null) Unauthorized();
                var state = Decode(stored!);
                // Fresh authorization/expiry check immediately before the CAS.
                // Revocation updates this same versioned manifest.
                Authorize(state, key, upload: true);
                previous = state.Runs.Find(run => run.RunId == runId);
                if (previous is not null) return Identical(previous, digest);
                if (state.Runs.Count >= options.MaxRunsPerSession) throw Quota();

                var run = new RunManifest(runId, state.Runs.Count == 0 ? 1 : state.Runs.Max(run => run.Sequence) + 1,
                    clock.GetUtcNow(), request.FinishedAtUtc, request.GameId, request.SongId,
                    request.ChartId, request.SampleIntervalSec, request.Autoplay, request.LatencyBasis,
                    request.SuccessfulHitCount, request.SummaryExceedsFormatRange, digest, prefix);
                var next = state with { Runs = [.. state.Runs, run] };
                commitAttempted = true;
                if (await store.WriteAsync(StateName(id), Encode(next), stored!.Version, ct))
                {
                    // Expiry can pass while the storage operation is in flight.
                    // Such data is never returned as a successful live publication.
                    RequireLive(next);
                    return (run, true);
                }
                // A definite precondition failure is safe. If a network failure
                // leaves the commit outcome unknown, never delete staged files.
                commitAttempted = false;
            }
            throw Busy();
        }
        finally
        {
            if (!commitAttempted)
            {
                // Failures here are handled by orphan cleanup, never by deleting
                // files under a prefix that another request could have published.
                try
                {
                    using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await store.DeleteAsync(prefix + "series.csv", null, cleanupDeadline.Token);
                    await store.DeleteAsync(prefix + "summary.csv", null, cleanupDeadline.Token);
                }
                catch { /* Retry-safe cleanup is performed by the cleanup worker. */ }
            }
        }
    }

    private static (RunManifest, bool) Identical(RunManifest previous, string digest)
    {
        if (!EqualHash(previous.Digest, digest)) throw Conflict("run_conflict", "Attempt already exists with different content.");
        return (previous, false);
    }

    public async Task<byte[]> DownloadAsync(string id, string runId, string filename, string? key, CancellationToken ct)
    {
        RequireId(runId);
        var state = await ReadAsync(id, key, upload: false, ct);
        var run = state.Runs.Find(run => run.RunId == runId);
        if (run is null || filename is not ("series.csv" or "summary.csv"))
            throw new SessionApiException(404, "run_not_found", "Completed attempt or file was not found.");
        var file = await store.ReadAsync(run.ObjectPrefix + filename, ct)
            ?? throw new IOException("Committed file is missing from storage.");
        // Recheck after storage I/O, including when the URL was obtained earlier.
        await ReadAsync(id, key, upload: false, ct);
        return file.Bytes;
    }

    public async Task RevokeAsync(string id, string? key, CancellationToken ct)
    {
        RequireId(id);
        for (int attempt = 0; attempt < 32; attempt++)
        {
            var stored = await store.ReadAsync(StateName(id), ct);
            if (stored is null) Unauthorized();
            var state = Decode(stored!);
            Authorize(state, key, upload: true);
            if (await store.WriteAsync(StateName(id), Encode(state with { RevokedAt = clock.GetUtcNow() }), stored!.Version, ct)) return;
        }
        throw Busy();
    }

    public async Task CleanupAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await foreach (var entry in store.ListAsync("states/", ct))
        {
            var stored = await store.ReadAsync(entry.Name, ct);
            if (stored is null) continue;
            var state = Decode(stored);
            var endedAt = state.RevokedAt is { } revoked && revoked < state.ExpiresAt ? revoked : state.ExpiresAt;
            if (endedAt > now) continue;
            if (!state.Purged)
            {
                if (!await store.WriteAsync(entry.Name, Encode(state with { Purged = true, Runs = [] }), stored.Version, ct)) continue;
                stored = await store.ReadAsync(entry.Name, ct);
                if (stored is null) continue;
                state = Decode(stored);
            }
            await foreach (var file in store.ListAsync($"data/{state.SessionId}/", ct))
                await store.DeleteAsync(file.Name, null, ct);

            if (now >= endedAt.AddHours(24))
            {
                await store.WriteAsync(RetiredName(state.SessionId), [], null, ct);
                await store.DeleteAsync(entry.Name, stored.Version, ct);
            }
        }

        // Active uploads have a two-minute HTTP deadline. Never touch young
        // staging objects; older objects not referenced by a manifest are orphans.
        await foreach (var entry in store.ListAsync("data/", ct))
        {
            if (entry.LastModified > now.AddHours(-1)) continue;
            string[] path = entry.Name.Split('/');
            if (path.Length != 5) continue;
            var stored = await store.ReadAsync(StateName(path[1]), ct);
            var state = stored is null ? null : Decode(stored);
            string prefix = string.Join('/', path.Take(4)) + "/";
            if (state is null || !state.Runs.Any(run => run.ObjectPrefix == prefix))
                await store.DeleteAsync(entry.Name, null, ct);
        }
    }

    private static void Unauthorized() => throw new SessionApiException(401, "unauthorized", "A valid session credential is required.");
    private static SessionApiException Conflict(string code, string message) => new(409, code, message);
    private static SessionApiException Busy() => new(503, "storage_busy", "Please retry the same request after a short delay.");
    private static SessionApiException Quota() => new(429, "session_quota_exceeded", "Session has reached its completed attempt limit.");
}

public sealed class SessionCleanupWorker(IServiceProvider services, SessionApiOptions options, ILogger<SessionCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(options.CleanupIntervalMinutes));
        do
        {
            try { await services.GetRequiredService<SessionService>().CleanupAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError("Session cleanup failed ({ErrorType}); check storage configuration and availability.", ex.GetType().Name); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
