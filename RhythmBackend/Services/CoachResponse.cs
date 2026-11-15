using System.Text.Json.Serialization;

namespace RhythmBackend.Services;

public sealed record CoachResponse
{
    [JsonPropertyName("mode")]
    public required string Mode { get; init; }

    [JsonPropertyName("tier")]
    public string? Tier { get; init; }

    [JsonPropertyName("coaching_focus")]
    public required string CoachingFocus { get; init; }

    [JsonPropertyName("speech")]
    public required string Speech { get; init; }

    [JsonPropertyName("phone_text")]
    public required string[] PhoneText { get; init; }

    [JsonPropertyName("drill")]
    public string? Drill { get; init; }

    [JsonPropertyName("next_target")]
    public string? NextTarget { get; init; }

    [JsonPropertyName("focus_section")]
    public FocusSection? FocusSection { get; init; }

    [JsonPropertyName("data_notes")]
    public required string[] DataNotes { get; init; }

    [JsonPropertyName("confidence")]
    public required string Confidence { get; init; }

    [JsonPropertyName("evidence_basis")]
    public required string EvidenceBasis { get; init; }
}

public sealed record FocusSection(
    [property: JsonPropertyName("start_s")] int StartS,
    [property: JsonPropertyName("end_s")] int EndS,
    [property: JsonPropertyName("reason")] string Reason);

public sealed class CoachResponseException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}
