using RhythmBackend.Services;
using System.Text.Json;

int checks = 0;
var valid = new CoachResponse
{
    Mode = "song_debrief",
    Tier = "B",
    CoachingFocus = "timing_bias",
    Speech = "Your average timing was positive, which means late. Focus on aligning your taps with the beat. Practice tapping along steadily, then replay the song with the goal of bringing your average offset closer to zero. During the next attempt, listen for the beat before each phrase, keep your hands relaxed, and review whether the timing moves consistently toward the center.",
    Observations = [new("timing", "+21 milliseconds", "Late timing was observed.")],
    PhoneText = ["Positive offset means late."],
    DataNotes = ["Summary only; section data is not available."],
    Drill = "Tap steadily with the beat.",
    NextTarget = "Bring average timing closer to zero.",
    Confidence = "low",
    EvidenceBasis = "practice_convention"
};

CoachResponseValidator.Validate(valid, "song_debrief", "B");
checks++;

void Reject(CoachResponse output, string name)
{
    try { CoachResponseValidator.Validate(output, "song_debrief", "B"); }
    catch (InvalidOperationException) { checks++; return; }
    throw new Exception($"Invalid coaching was accepted: {name}");
}

Reject(valid with { Mode = "chat" }, "wrong mode");
Reject(valid with { Tier = "A" }, "wrong tier");
Reject(valid with { Speech = "Too short." }, "short speech");
Reject(valid with { Speech = string.Join(' ', Enumerable.Repeat("word", 121)) }, "long speech");
Reject(valid with { Speech = valid.Speech + " [1]" }, "citation");
Reject(valid with { Speech = valid.Speech + " https://example.com" }, "link");
Reject(valid with { FocusSection = new(1, 2, "unsupported") }, "section without series");
foreach (string focus in new[] { "timing_spread", "recovery_after_miss", "density_or_difficulty" })
    Reject(valid with { CoachingFocus = focus }, focus);
Reject(valid with { Confidence = "certain" }, "confidence enum");
Reject(valid with { EvidenceBasis = "invented" }, "evidence enum");
Reject(valid with { PhoneText = null! }, "null phone text");
Reject(valid with { DataNotes = [null!] }, "null data note");

foreach (int words in new[] { 60, 90 })
{
    CoachResponseValidator.Validate(valid with { Speech = string.Join(' ', Enumerable.Repeat("word", words)) }, "song_debrief", "B");
    checks++;
}

var chat = valid with { Mode = "chat", Tier = null, CoachingFocus = "none", Speech = "Practice tapping steadily with the beat." };
CoachResponseValidator.Validate(chat, "chat", null);
checks++;

var generalComfort = chat with
{
    CoachingFocus = "none",
    Speech = "Pause playing if it causes pain. Try a relaxed grip and a comfortable setup when you feel comfortable again. This is general information, not a diagnosis; seek medical advice if symptoms persist or worsen.",
    Drill = null,
    NextTarget = null,
    FocusSection = null,
    Confidence = "low",
    EvidenceBasis = "practice_convention"
};
CoachResponseValidator.Validate(generalComfort, "chat", null);
checks++;

// Symptom words must not reject a helpful limitation or referral on their own.
CoachResponseValidator.Validate(generalComfort with
{
    Speech = "I cannot diagnose an injury from game statistics. If symptoms persist or worsen, ask a qualified health professional for medical advice."
}, "chat", null);
checks++;

try
{
    CoachResponseValidator.Validate(chat with { Speech = "too short" }, "chat", null);
    throw new Exception("Invalid chat speech was accepted.");
}
catch (CoachResponseException ex) when (ex.Code == "coach_speech_rejected") { checks++; }

string json = JsonSerializer.Serialize(valid);
using var document = JsonDocument.Parse(json);
if (!document.RootElement.TryGetProperty("phone_text", out _) || !document.RootElement.TryGetProperty("evidence_basis", out _))
    throw new Exception("Response JSON field names do not match the supplied schema.");
checks++;

try { JsonSerializer.Deserialize<CoachResponse>("{}"); throw new Exception("Missing required fields were accepted."); }
catch (JsonException) { checks++; }

Console.WriteLine($"Passed {checks} coaching contract checks.");
