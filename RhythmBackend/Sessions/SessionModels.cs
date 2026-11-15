using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RhythmBackend.Sessions;

public sealed class SessionApiOptions
{
    public string StorageProvider { get; set; } = "AzureBlob";
    public string? StorageConnectionString { get; set; }
    public string ContainerName { get; set; } = "rhythm-sessions";
    public string? LocalDirectory { get; set; }
    public int LifetimeHours { get; set; } = 24;
    public int CleanupIntervalMinutes { get; set; } = 15;
    public int MaxRunsPerSession { get; set; } = 200;
    public bool QrLoggingRedactionConfirmed { get; set; }
    public string[] AllowedOrigins { get; set; } = ["https://seaweedbrainn.github.io"];
}

public static class SessionJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new UtcTimestampConverter() }
    };

    private sealed class UtcTimestampConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
                throw new JsonException("A UTC timestamp string is required.");
            string? value = reader.GetString();
            if (value is null || !value.EndsWith('Z') || !reader.TryGetDateTimeOffset(out var parsed))
                throw new JsonException("A UTC timestamp ending in Z is required.");
            return parsed.ToUniversalTime();
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
    }
}

public sealed record RegisterSessionRequest(
    [property: JsonRequired] int ContractVersion,
    [property: JsonRequired] string SessionId,
    [property: JsonRequired] string SessionKey,
    [property: JsonRequired] string UploadKey);

public sealed record UploadRunRequest(
    [property: JsonRequired] int ContractVersion,
    [property: JsonRequired] DateTimeOffset FinishedAtUtc,
    [property: JsonRequired] int GameId,
    [property: JsonRequired] int SongId,
    [property: JsonRequired] int ChartId,
    [property: JsonRequired] int SampleIntervalSec,
    [property: JsonRequired] bool Autoplay,
    [property: JsonRequired] string LatencyBasis,
    [property: JsonRequired] int SuccessfulHitCount,
    [property: JsonRequired] bool SummaryExceedsFormatRange,
    [property: JsonRequired] string SeriesCsv,
    [property: JsonRequired] string SummaryCsv);

public sealed record SessionManifest(
    string SessionId,
    string ReadKeyHash,
    string UploadKeyHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? RevokedAt,
    bool Purged,
    List<RunManifest> Runs);

public sealed record RunManifest(
    string RunId,
    long Sequence,
    DateTimeOffset CompletedAt,
    DateTimeOffset FinishedAtUtc,
    int GameId,
    int SongId,
    int ChartId,
    int SampleIntervalSec,
    bool Autoplay,
    string LatencyBasis,
    int SuccessfulHitCount,
    bool SummaryExceedsFormatRange,
    string Digest,
    string ObjectPrefix);

public sealed class SessionApiException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

public sealed record StoredObject(byte[] Bytes, string Version, DateTimeOffset LastModified);
public sealed record ObjectEntry(string Name, DateTimeOffset LastModified);

public interface ISessionObjectStore
{
    Task<StoredObject?> ReadAsync(string name, CancellationToken ct);
    // null means create-only; a version means compare-and-swap, never an unconditional overwrite.
    Task<bool> WriteAsync(string name, byte[] bytes, string? expectedVersion, CancellationToken ct);
    Task DeleteAsync(string name, string? expectedVersion, CancellationToken ct);
    IAsyncEnumerable<ObjectEntry> ListAsync(string prefix, CancellationToken ct);
}
