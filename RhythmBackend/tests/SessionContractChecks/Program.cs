using RhythmBackend.Sessions;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

try
{
// Test utility for this process's local Azurite instance only; never accepts a
// caller-provided Azure connection string or account.
if (args.Contains("--reset-emulator-retention"))
{
    var emulator = new Azure.Storage.Blobs.BlobServiceClient("UseDevelopmentStorage=true");
    var properties = (await emulator.GetPropertiesAsync()).Value;
    properties.DeleteRetentionPolicy = new Azure.Storage.Blobs.Models.BlobRetentionPolicy { Enabled = false };
    await emulator.SetPropertiesAsync(properties);
    Console.WriteLine("Reset local emulator test retention.");
    return;
}

int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    checks++;
}
async Task Reject(Func<Task> action, int status, string message)
{
    try { await action(); }
    catch (SessionApiException ex) when (ex.Status == status) { checks++; return; }
    throw new Exception(message);
}
void Invalid(UploadRunRequest request, int status, string message)
{
    try { RunCsvValidator.Validate(request); }
    catch (SessionApiException ex) when (ex.Status == status) { checks++; return; }
    throw new Exception(message);
}

string Key() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_');
string Id() => Guid.NewGuid().ToString("N");
RegisterSessionRequest Registration() => new(1, Id(), Key(), Key());
UploadRunRequest Sample(int interval = 1) => new(1, new DateTimeOffset(2026, 11, 15, 4, 0, 0, TimeSpan.Zero),
    2, 103, 1, interval, false, "successful_hits_only_v1", 1, false,
    $"game_id,2\nsong_id,103\nchart_id,1\nbpm,120.4\nsample_interval_sec,{interval}\ngame_time,percent_accuracy,avg_latency,bpm,nps,latency_spread\n0,,,120.4,0,\n{interval},100,-12,120.4,1,\n",
    $"02,103,3,{interval * 2000},120,1,1000000,-12");

var clock = new TestClock();
string testRoot = Path.Combine(Path.GetTempPath(), "rhythm-session-tests-" + Id());
Directory.CreateDirectory(testRoot);
bool useBlob = args.Contains("--blob");
var blobOptions = new SessionApiOptions { StorageConnectionString = "UseDevelopmentStorage=true", ContainerName = "rhythmchecks-" + Id() };
ISessionObjectStore local = useBlob ? new AzureSessionObjectStore(blobOptions, allowLocalEmulator: true)
    : new LocalSessionObjectStore(Path.Combine(testRoot, "store"));
var faults = new FaultStore(local);
var options = new SessionApiOptions { MaxRunsPerSession = 200 };
var service = new SessionService(faults, options, clock);

var registration = Registration();
var created = await service.RegisterAsync(registration, default);
var retry = await service.RegisterAsync(registration, default);
Check(created.Created && !retry.Created && created.State.CreatedAt == retry.State.CreatedAt
    && created.State.ExpiresAt == retry.State.ExpiresAt, "Registration retry changed original identity/timestamps.");
Check(!Encoding.UTF8.GetString((await local.ReadAsync($"states/{registration.SessionId}.json", default))!.Bytes).Contains(registration.SessionKey),
    "Plaintext read credential entered storage.");
Check(!Encoding.UTF8.GetString((await local.ReadAsync($"states/{registration.SessionId}.json", default))!.Bytes).Contains(registration.UploadKey),
    "Plaintext upload credential entered storage.");
var lostRegistration = Registration();
faults.LoseRegistrationResponseOnce = true;
try { await service.RegisterAsync(lostRegistration, default); throw new Exception("Registration fault did not fire."); }
catch (IOException) { checks++; }
Check(!(await service.RegisterAsync(lostRegistration, default)).Created, "Lost registration response could not be recovered safely.");
await Reject(() => service.RegisterAsync(registration with { UploadKey = Key() }, default), 409, "Registration overwrote credentials.");
await Reject(() => service.RegisterAsync(registration with { UploadKey = registration.SessionKey }, default), 400, "Read/write keys were allowed to be identical.");
await Reject(() => service.ReadAsync(registration.SessionId, Key(), false, default), 401, "Wrong key read session.");
await Reject(() => service.ReadAsync(Id(), registration.SessionKey, false, default), 401, "Unknown session exposed existence.");
await Reject(() => service.ReadAsync(registration.SessionId, registration.UploadKey, false, default), 403, "Upload key granted read permission.");
await Reject(() => service.ReadAsync(registration.SessionId, registration.SessionKey, true, default), 403, "Read key granted upload permission.");

var sample = Sample();
try
{
    JsonSerializer.Deserialize<UploadRunRequest>(JsonSerializer.Serialize(sample, SessionJson.Options)
        .Replace("\"2026-11-15T04:00:00.0000000Z\"", "123"), SessionJson.Options);
    throw new Exception("Non-string timestamp was accepted.");
}
catch (JsonException) { checks++; }
RunCsvValidator.Validate(sample);
checks++;
RunCsvValidator.Validate(Sample(2));
checks++;
RunCsvValidator.Validate(sample with { SeriesCsv = sample.SeriesCsv.Replace("\n", "\r\n"), SummaryCsv = sample.SummaryCsv + "\r\n" });
checks++;
var zero = sample with { SuccessfulHitCount = 0, SeriesCsv = sample.SeriesCsv.Replace("100,-12", "0,"), SummaryCsv = sample.SummaryCsv[..^3] + "0" };
RunCsvValidator.Validate(zero);
checks++;
RunCsvValidator.Validate(sample with { SeriesCsv = sample.SeriesCsv.Replace("100,-12", "100,0"), SummaryCsv = sample.SummaryCsv[..^3] + "0" });
checks++;
Invalid(sample with { SeriesCsv = sample.SeriesCsv.Replace("100,-12", "101,-12") }, 422, "Invalid accuracy accepted.");
Invalid(sample with { SeriesCsv = sample.SeriesCsv.Replace("100,-12", "100,NaN") }, 422, "Non-finite CSV accepted.");
Invalid(sample with { SeriesCsv = sample.SeriesCsv.Replace("120.4,1,\n", "120.4,1,5\n") }, 422, "One-hit spread accepted.");
Invalid(sample with { SuccessfulHitCount = 0 }, 422, "No-hit latency was treated as a real measurement.");
Invalid(sample with { SampleIntervalSec = 2 }, 422, "Envelope/series mismatch accepted.");
Invalid(sample with { ContractVersion = 2 }, 422, "Unknown contract version accepted.");
Invalid(sample with { LatencyBasis = "includes_misses" }, 422, "Unfixed latency basis accepted.");
Invalid(sample with { SeriesCsv = new string('a', RunCsvValidator.MaxCsvBytes + 1) }, 413, "CSV byte limit not enforced.");
Invalid(sample with { SeriesCsv = new string('é', RunCsvValidator.MaxCsvBytes / 2 + 1) }, 413, "CSV limit counted characters instead of UTF-8 bytes.");
Invalid(sample with { SummaryCsv = sample.SummaryCsv.Replace(",1,1000000", ",2,1000000") }, 422, "Summary/series note mismatch accepted.");
Invalid(sample with { SeriesCsv = sample.SeriesCsv.Replace("\n1,100", "\n2,100") }, 422, "Broken sample cadence accepted.");
var saturated = sample with { GameId = 123, SongId = 1234, SummaryExceedsFormatRange = true,
    SeriesCsv = sample.SeriesCsv.Replace("game_id,2\n", "game_id,123\n").Replace("song_id,103\n", "song_id,1234\n"),
    SummaryCsv = sample.SummaryCsv.Replace("02,103,", "99,999,") };
RunCsvValidator.Validate(saturated);
checks++;
Invalid(saturated with { SummaryExceedsFormatRange = false }, 422, "Unflagged saturation accepted.");
Invalid(saturated with { SummaryCsv = saturated.SummaryCsv.Replace("99,999,", "98,999,") }, 422, "Unexplained saturated identity mismatch accepted.");
RunCsvValidator.Validate(sample with {
    SeriesCsv = sample.SeriesCsv.Replace("1,100,-12,120.4,1,", "1,50,-12,120.4,2,"),
    SummaryCsv = sample.SummaryCsv.Replace(",1,1000000,", ",2,500000,")
});
checks++; // One hit plus a miss: retain total notes, do not fabricate timing spread.
RunCsvValidator.Validate(sample with { SummaryExceedsFormatRange = true,
    SeriesCsv = sample.SeriesCsv.Replace("1,100,-12,120.4,1,", "1,0.03,-12,120.4,3001,"),
    SummaryCsv = sample.SummaryCsv.Replace(",1,1000000,", ",3000,500000,")
});
checks++;

string runId = Id();
var first = await service.UploadAsync(registration.SessionId, runId, registration.UploadKey, sample, default);
var duplicate = await service.UploadAsync(registration.SessionId, runId, registration.UploadKey, sample, default);
Check(first.Created && !duplicate.Created && first.Run == duplicate.Run, "Run retry changed sequence/completion time.");
var before = await service.DownloadAsync(registration.SessionId, runId, "series.csv", registration.SessionKey, default);
Check(before.SequenceEqual(Encoding.UTF8.GetBytes(sample.SeriesCsv)), "Downloaded series differs from uploaded bytes.");
await Reject(() => service.UploadAsync(registration.SessionId, runId, registration.UploadKey,
    sample with { SummaryCsv = sample.SummaryCsv + "\n" }, default), 409, "Changed CSV bytes overwrote a committed run.");
await Reject(() => service.UploadAsync(registration.SessionId, runId, registration.UploadKey,
    sample with { Autoplay = true }, default), 409, "Changed parsed metadata overwrote a committed run.");
var others = Registration();
await service.RegisterAsync(others, default);
await Reject(() => service.DownloadAsync(registration.SessionId, runId, "summary.csv", others.SessionKey, default), 401, "Another session's key downloaded CSV.");
await Reject(() => service.DownloadAsync(registration.SessionId, runId, "other.csv", registration.SessionKey, default), 404, "Arbitrary filename exposed storage.");
await Reject(() => service.DownloadAsync(registration.SessionId, Id(), "summary.csv", registration.SessionKey, default), 404, "Unpublished attempt was available.");

string concurrentId = Id();
var simultaneous = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => service.UploadAsync(
    registration.SessionId, concurrentId, registration.UploadKey, sample, default)));
Check(simultaneous.Count(result => result.Created) == 1 && simultaneous.All(result => result.Run.Sequence == 2), "Concurrent retries duplicated attempts.");
Check((await service.ReadAsync(registration.SessionId, registration.SessionKey, false, default)).Runs.Count == 2, "Concurrent retry listing count is wrong.");
Check((await service.DownloadAsync(registration.SessionId, concurrentId, "summary.csv", registration.SessionKey, default))
    .SequenceEqual(Encoding.UTF8.GetBytes(sample.SummaryCsv)), "Losing concurrent retry deleted winning files.");

string failedId = Id();
faults.FailSummaryOnce = true;
try { await service.UploadAsync(registration.SessionId, failedId, registration.UploadKey, sample, default); throw new Exception("Fault injection did not fire."); }
catch (IOException) { checks++; }
await Reject(() => service.DownloadAsync(registration.SessionId, failedId, "series.csv", registration.SessionKey, default), 404, "Partial upload was downloadable.");
Check(!(await service.ReadAsync(registration.SessionId, registration.SessionKey, false, default)).Runs.Any(run => run.RunId == failedId), "Partial upload was listed.");
Check((await service.UploadAsync(registration.SessionId, failedId, registration.UploadKey, sample, default)).Created, "Partial upload could not be retried.");

string ambiguousId = Id();
faults.LoseCommitResponseOnce = true;
try { await service.UploadAsync(registration.SessionId, ambiguousId, registration.UploadKey, sample, default); throw new Exception("Ambiguous fault did not fire."); }
catch (IOException) { checks++; }
var recovered = await service.UploadAsync(registration.SessionId, ambiguousId, registration.UploadKey, sample, default);
Check(!recovered.Created, "Lost commit response produced a duplicate on retry.");
Check((await service.DownloadAsync(registration.SessionId, ambiguousId, "series.csv", registration.SessionKey, default)).SequenceEqual(before), "Unknown commit outcome deleted committed files.");

// A new service instance reads the same durable files after a process-equivalent restart.
var restarted = new SessionService(useBlob ? new AzureSessionObjectStore(blobOptions, allowLocalEmulator: true)
    : new LocalSessionObjectStore(Path.Combine(testRoot, "store") + Path.DirectorySeparatorChar), options, clock);
Check((await restarted.ReadAsync(registration.SessionId, registration.SessionKey, false, default)).Runs.Count == 4, "Session data did not survive store recreation.");

var raceRegistration = Registration();
await service.RegisterAsync(raceRegistration, default);
var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
faults.BeforeRunCommit = async () => { entered.SetResult(); await release.Task; };
var racingUpload = service.UploadAsync(raceRegistration.SessionId, Id(), raceRegistration.UploadKey, sample, default);
await entered.Task;
await service.RevokeAsync(raceRegistration.SessionId, raceRegistration.UploadKey, default);
release.SetResult();
await Reject(async () => await racingUpload, 410, "Upload published after concurrent revocation.");
await Reject(() => service.ReadAsync(raceRegistration.SessionId, raceRegistration.SessionKey, false, default), 410, "Revocation did not invalidate read key.");
await Reject(() => service.RegisterAsync(raceRegistration, default), 410, "Registration revived revoked session.");

var quotaService = new SessionService(local, new SessionApiOptions { MaxRunsPerSession = 1 }, clock);
await Reject(() => quotaService.UploadAsync(registration.SessionId, Id(), registration.UploadKey, sample, default), 429, "Session quota not enforced.");
Check(!(await quotaService.UploadAsync(registration.SessionId, runId, registration.UploadKey, sample, default)).Created, "Identical retry was blocked by full-session quota.");

clock.Advance(TimeSpan.FromHours(25));
await Reject(() => service.ReadAsync(registration.SessionId, registration.SessionKey, false, default), 410, "Expired session remained readable.");
await Reject(() => service.UploadAsync(registration.SessionId, Id(), registration.UploadKey, sample, default), 410, "Expired session remained writable.");
await Reject(() => service.RegisterAsync(registration, default), 410, "Registration extended expired session.");
await service.CleanupAsync(default);
var expiredState = await local.ReadAsync($"states/{registration.SessionId}.json", default);
Check(expiredState is not null && !Encoding.UTF8.GetString(expiredState.Bytes).Contains(runId), "Cleanup retained run metadata.");
var remaining = new List<ObjectEntry>();
await foreach (var entry in local.ListAsync("data/", default)) remaining.Add(entry);
Check(remaining.Count == 0, "Cleanup retained expired CSVs/staging objects.");
clock.Advance(TimeSpan.FromHours(24));
await service.CleanupAsync(default);
Check(await local.ReadAsync($"states/{registration.SessionId}.json", default) is null, "Expired session keys/metadata were retained beyond cleanup window.");
await Reject(() => service.RegisterAsync(registration, default), 409, "Purged session ID could be revived.");

if (args.Length > 0)
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    start.ArgumentList.Add(Path.GetFullPath(args[0]));
    start.ArgumentList.Add("--urls");
    start.ArgumentList.Add($"http://127.0.0.1:{port}");
    start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
    start.Environment["SessionApi__StorageProvider"] = "Local";
    start.Environment["SessionApi__LocalDirectory"] = Path.Combine(testRoot, "http-store");
    string maintenanceKey = Key();
    start.Environment["SessionApi__MaintenanceKey"] = maintenanceKey;
    if (useBlob)
    {
        start.Environment["SessionApi__StorageProvider"] = "AzureBlob";
        start.Environment["SessionApi__StorageConnectionString"] = blobOptions.StorageConnectionString;
        start.Environment["SessionApi__ContainerName"] = blobOptions.ContainerName;
    }
    using var process = Process.Start(start) ?? throw new Exception("Could not start HTTP test host.");
    var logsTask = process.StandardOutput.ReadToEndAsync();
    var errorsTask = process.StandardError.ReadToEndAsync();
    try
    {
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };
        bool ready = false;
        for (int i = 0; i < 80; i++)
        {
            if (process.HasExited) throw new Exception("HTTP test host exited before startup.");
            try { ready = (await http.GetAsync("/")).IsSuccessStatusCode; } catch (HttpRequestException) { }
            if (ready) break;
            await Task.Delay(100);
        }
        Check(ready, "HTTP host did not start.");
        async Task<HttpResponseMessage> Send(HttpMethod method, string path, string? bearer = null, object? body = null)
        {
            using var message = new HttpRequestMessage(method, path);
            if (bearer is not null) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            if (body is not null) message.Content = new StringContent(JsonSerializer.Serialize(body, SessionJson.Options), Encoding.UTF8, "application/json");
            return await http.SendAsync(message);
        }
        var httpRegistration = Registration();
        var registered = await Send(HttpMethod.Post, "/v1/sessions", body: httpRegistration);
        Check(registered.StatusCode == HttpStatusCode.Created, "HTTP registration failed.");
        using var regJson = JsonDocument.Parse(await registered.Content.ReadAsStringAsync());
        Check(regJson.RootElement.GetProperty("created_at").GetString()!.EndsWith('Z'), "UTC timestamp did not use Z.");
        Check(!regJson.RootElement.TryGetProperty("session_key", out _), "Registration echoed credentials.");
        Check((await Send(HttpMethod.Post, "/v1/sessions", body: httpRegistration)).StatusCode == HttpStatusCode.OK, "HTTP registration retry failed.");
        string root = $"/v1/sessions/{httpRegistration.SessionId}";
        var empty = await Send(HttpMethod.Get, root + "/runs", httpRegistration.SessionKey);
        using var emptyJson = JsonDocument.Parse(await empty.Content.ReadAsStringAsync());
        Check(emptyJson.RootElement.GetProperty("runs").GetArrayLength() == 0 && emptyJson.RootElement.GetProperty("next_after_sequence").GetInt64() == 0, "Empty session listing is incorrect.");
        string httpRun = Id();
        Check((await Send(HttpMethod.Put, root + "/runs/" + httpRun, httpRegistration.UploadKey, sample)).StatusCode == HttpStatusCode.Created, "HTTP upload failed.");
        Check((await Send(HttpMethod.Put, root + "/runs/" + httpRun, httpRegistration.UploadKey, sample)).StatusCode == HttpStatusCode.OK, "HTTP upload retry failed.");
        var reordered = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(sample, SessionJson.Options))!
            .Reverse().ToDictionary(pair => pair.Key, pair => pair.Value);
        Check((await Send(HttpMethod.Put, root + "/runs/" + httpRun, httpRegistration.UploadKey, reordered)).StatusCode == HttpStatusCode.OK, "JSON property order changed upload identity.");
        Check((await Send(HttpMethod.Put, root + "/runs/" + Id(), httpRegistration.UploadKey, sample with { FinishedAtUtc = sample.FinishedAtUtc.AddMinutes(-5) })).StatusCode == HttpStatusCode.Created, "Delayed attempt failed.");
        Check((await Send(HttpMethod.Put, root + "/runs/" + Id(), httpRegistration.UploadKey, sample with { Autoplay = true })).StatusCode == HttpStatusCode.Created, "Autoplay attempt could not be retained.");
        var page = await Send(HttpMethod.Get, root + "/runs?limit=1", httpRegistration.SessionKey);
        using var pageJson = JsonDocument.Parse(await page.Content.ReadAsStringAsync());
        Check(pageJson.RootElement.GetProperty("has_more").GetBoolean() && pageJson.RootElement.GetProperty("next_after_sequence").GetInt64() == 1, "Pagination skipped or misordered attempts.");
        string downloadPath = pageJson.RootElement.GetProperty("runs")[0].GetProperty("files").GetProperty("series").GetString()!;
        var csv = await Send(HttpMethod.Get, downloadPath, httpRegistration.SessionKey);
        Check((await csv.Content.ReadAsByteArrayAsync()).SequenceEqual(Encoding.UTF8.GetBytes(sample.SeriesCsv)), "HTTP CSV content changed.");
        Check(csv.Content.Headers.ContentType?.MediaType == "text/csv" && csv.Headers.CacheControl?.NoStore == true, "CSV MIME/cache headers are wrong.");
        using (var conditional = new HttpRequestMessage(HttpMethod.Get, downloadPath))
        {
            conditional.Headers.TryAddWithoutValidation("If-None-Match", "*");
            Check((await http.SendAsync(conditional)).StatusCode == HttpStatusCode.Unauthorized, "Conditional request bypassed authorization.");
        }
        Check((await Send(HttpMethod.Get, root + "/runs?key=" + httpRegistration.SessionKey)).StatusCode == HttpStatusCode.BadRequest, "API accepted query key.");
        Check((await Send(HttpMethod.Get, root + "/runs", httpRegistration.UploadKey)).StatusCode == HttpStatusCode.Forbidden, "HTTP upload key read data.");
        Check((await Send(HttpMethod.Get, root + "/runs?after_sequence=-1", httpRegistration.SessionKey)).StatusCode == HttpStatusCode.BadRequest, "Negative cursor accepted.");
        using (var preflight = new HttpRequestMessage(HttpMethod.Options, root + "/runs/" + httpRun))
        {
            preflight.Headers.Add("Origin", "https://seaweedbrainn.github.io");
            preflight.Headers.Add("Access-Control-Request-Method", "PUT");
            preflight.Headers.Add("Access-Control-Request-Headers", "authorization,content-type");
            var preflightResponse = await http.SendAsync(preflight);
            Check(preflightResponse.StatusCode == HttpStatusCode.NoContent && preflightResponse.Headers.GetValues("Access-Control-Allow-Origin").Single() == "https://seaweedbrainn.github.io", "Authenticated-upload preflight failed.");
        }
        using (var disallowed = new HttpRequestMessage(HttpMethod.Get, root + "/runs"))
        {
            disallowed.Headers.Add("Origin", "https://untrusted.invalid");
            disallowed.Headers.Authorization = new AuthenticationHeaderValue("Bearer", httpRegistration.SessionKey);
            var disallowedResponse = await http.SendAsync(disallowed);
            Check(!disallowedResponse.Headers.Contains("Access-Control-Allow-Origin"), "Unapproved browser origin received CORS permission.");
        }
        var landing = await http.GetAsync($"/sessions/{httpRegistration.SessionId}/?key={httpRegistration.SessionKey}");
        string html = await landing.Content.ReadAsStringAsync();
        Check(landing.IsSuccessStatusCode && html.Contains("history.replaceState") && !html.Contains(httpRegistration.SessionKey)
            && landing.Headers.GetValues("Referrer-Policy").Single() == "no-referrer", "Landing leaked/consumed key or did not strip the URL query.");
        var schema = await http.GetAsync("/openapi.json");
        Check(schema.IsSuccessStatusCode, "OpenAPI document was not available.");
        foreach (string filename in new[] { "series.csv", "summary.csv" })
        {
            Check((await Send(HttpMethod.Get, root + "/runs/" + httpRun + "/" + filename)).StatusCode == HttpStatusCode.Unauthorized, "CSV download accepted no credential.");
            Check((await Send(HttpMethod.Get, root + "/runs/" + httpRun + "/" + filename, httpRegistration.UploadKey)).StatusCode == HttpStatusCode.Forbidden, "CSV download accepted upload scope.");
        }
        using (var tooBig = new HttpRequestMessage(HttpMethod.Put, root + "/runs/" + Id()))
        {
            tooBig.Headers.Authorization = new AuthenticationHeaderValue("Bearer", httpRegistration.UploadKey);
            tooBig.Content = new StringContent(new string('x', RunCsvValidator.MaxRequestBytes + 1), Encoding.UTF8, "application/json");
            Check((await http.SendAsync(tooBig)).StatusCode == HttpStatusCode.RequestEntityTooLarge, "Total JSON size limit failed.");
        }
        using (var missing = new HttpRequestMessage(HttpMethod.Put, root + "/runs/" + Id()))
        {
            missing.Headers.Authorization = new AuthenticationHeaderValue("Bearer", httpRegistration.UploadKey);
            missing.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            Check((await http.SendAsync(missing)).StatusCode == HttpStatusCode.BadRequest, "Required envelope fields were defaulted instead of rejected.");
        }
        Check((await Send(HttpMethod.Post, "/internal/session-cleanup")).StatusCode == HttpStatusCode.Unauthorized, "Cleanup accepted no operator credential.");
        Check((await Send(HttpMethod.Post, "/internal/session-cleanup", maintenanceKey)).StatusCode == HttpStatusCode.OK, "Authenticated cleanup request failed.");
        // Cross the real 100-record page boundary, retaining delayed/autoplay runs.
        for (int i = 0; i < 98; i++)
            Check((await Send(HttpMethod.Put, root + "/runs/" + Id(), httpRegistration.UploadKey, sample)).StatusCode == HttpStatusCode.Created, "Could not populate full pagination test.");
        using (var fullPage = JsonDocument.Parse(await (await Send(HttpMethod.Get, root + "/runs", httpRegistration.SessionKey)).Content.ReadAsStringAsync()))
            Check(fullPage.RootElement.GetProperty("runs").GetArrayLength() == 100
                && fullPage.RootElement.GetProperty("has_more").GetBoolean()
                && fullPage.RootElement.GetProperty("next_after_sequence").GetInt64() == 100, "Default 100-record page boundary failed.");
        using (var finalPage = JsonDocument.Parse(await (await Send(HttpMethod.Get, root + "/runs?after_sequence=100", httpRegistration.SessionKey)).Content.ReadAsStringAsync()))
            Check(finalPage.RootElement.GetProperty("runs").GetArrayLength() == 1
                && !finalPage.RootElement.GetProperty("has_more").GetBoolean(), "Continuation after 100 records failed.");
        using (var noPage = JsonDocument.Parse(await (await Send(HttpMethod.Get, root + "/runs?after_sequence=123", httpRegistration.SessionKey)).Content.ReadAsStringAsync()))
            Check(noPage.RootElement.GetProperty("runs").GetArrayLength() == 0
                && noPage.RootElement.GetProperty("next_after_sequence").GetInt64() == 123, "Empty page did not preserve cursor.");
        Check((await Send(HttpMethod.Delete, root, httpRegistration.SessionKey)).StatusCode == HttpStatusCode.Forbidden, "Read key revoked session.");
        Check((await Send(HttpMethod.Delete, root, httpRegistration.UploadKey)).StatusCode == HttpStatusCode.NoContent, "HTTP revocation failed.");
        Check((await Send(HttpMethod.Get, downloadPath, httpRegistration.SessionKey)).StatusCode == HttpStatusCode.Gone, "Previously obtained download URL survived revocation.");
        Check((await Send(HttpMethod.Get, root + "/runs", httpRegistration.SessionKey)).StatusCode == HttpStatusCode.Gone, "Revoked session remained listable.");
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
        string logs = await logsTask + await errorsTask;
        Check(!logs.Contains(httpRegistration.SessionKey) && !logs.Contains(httpRegistration.UploadKey), "Test credentials appeared in application logs.");
    }
    finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
}
if (useBlob)
{
    var account = new Azure.Storage.Blobs.BlobServiceClient("UseDevelopmentStorage=true");
    var container = account.GetBlobContainerClient(blobOptions.ContainerName);
    try
    {
        await container.SetAccessPolicyAsync(Azure.Storage.Blobs.Models.PublicAccessType.Blob);
        try
        {
            await new AzureSessionObjectStore(blobOptions, allowLocalEmulator: true).ReadAsync("probe", default);
            throw new Exception("Public container was accepted.");
        }
        catch (InvalidOperationException) { checks++; }
    }
    finally { await container.SetAccessPolicyAsync(Azure.Storage.Blobs.Models.PublicAccessType.None); }
    var original = (await account.GetPropertiesAsync()).Value;
    try
    {
        var enabled = (await account.GetPropertiesAsync()).Value;
        enabled.DeleteRetentionPolicy = new Azure.Storage.Blobs.Models.BlobRetentionPolicy { Enabled = true, Days = 7 };
        await account.SetPropertiesAsync(enabled);
        try
        {
            await new AzureSessionObjectStore(blobOptions, allowLocalEmulator: true).ReadAsync("probe", default);
            throw new Exception("Storage retention incompatible with deletion deadline was accepted.");
        }
        catch (InvalidOperationException) { checks++; }
    }
    finally
    {
        // An absent policy in the initial response is not an instruction to turn
        // retention off: send false explicitly to reset a formerly enabled policy.
        original.DeleteRetentionPolicy = new Azure.Storage.Blobs.Models.BlobRetentionPolicy { Enabled = false };
        await account.SetPropertiesAsync(original);
    }
}
Console.WriteLine($"Passed {checks} session/CSV/storage/HTTP contract checks using {(useBlob ? "Azurite + Azure SDK" : "local files")}. Failures are simulated; not live Azure or hardware verification.");
Console.WriteLine($"Disposable test data: {testRoot}");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Session contract checks failed: {ex.GetType().Name}: {ex.Message}");
    Environment.ExitCode = 1;
}

sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 15, 4, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan amount) => _now += amount;
}

sealed class FaultStore(ISessionObjectStore inner) : ISessionObjectStore
{
    public bool FailSummaryOnce { get; set; }
    public bool LoseCommitResponseOnce { get; set; }
    public bool LoseRegistrationResponseOnce { get; set; }
    public Func<Task>? BeforeRunCommit { get; set; }
    public Task<StoredObject?> ReadAsync(string name, CancellationToken ct) => inner.ReadAsync(name, ct);
    public async Task<bool> WriteAsync(string name, byte[] bytes, string? expected, CancellationToken ct)
    {
        if (name.EndsWith("summary.csv") && FailSummaryOnce) { FailSummaryOnce = false; throw new IOException("Simulated second-file storage failure."); }
        if (name.StartsWith("states/") && expected is not null && BeforeRunCommit is { } hook)
        {
            BeforeRunCommit = null;
            await hook();
        }
        bool result = await inner.WriteAsync(name, bytes, expected, ct);
        if (result && name.StartsWith("states/") && expected is null && LoseRegistrationResponseOnce)
        {
            LoseRegistrationResponseOnce = false;
            throw new IOException("Simulated lost registration acknowledgement.");
        }
        if (result && name.StartsWith("states/") && expected is not null && LoseCommitResponseOnce)
        {
            LoseCommitResponseOnce = false;
            throw new IOException("Simulated lost manifest commit acknowledgement.");
        }
        return result;
    }
    public Task DeleteAsync(string name, string? expected, CancellationToken ct) => inner.DeleteAsync(name, expected, ct);
    public IAsyncEnumerable<ObjectEntry> ListAsync(string prefix, CancellationToken ct) => inner.ListAsync(prefix, ct);
}
