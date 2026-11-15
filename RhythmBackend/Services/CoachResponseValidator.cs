using System.Text.RegularExpressions;

namespace RhythmBackend.Services;

public static class CoachResponseValidator
{
    // Mechanical checks from the supplied package. Semantic grounding still depends
    // on the system prompt; these checks do not establish scientific evidence.
    public static void Validate(CoachResponse output, string mode, string? tier)
    {
        bool valid = output.Mode == mode && output.Tier == tier
            && output.CoachingFocus is "timing_bias" or "timing_spread" or "calibration_check"
                or "density" or "recovery_after_miss" or "fatigue" or "none"
            && output.EvidenceBasis is "meta_analysis" or "lab_consensus" or "mixed_evidence"
                or "practice_convention" or "game_heuristic" or "none"
            && output.Confidence is "low" or "medium" or "high"
            && !string.IsNullOrWhiteSpace(output.Speech)
            && output.PhoneText is not null && output.PhoneText.All(line => line is not null)
            && output.DataNotes is not null && output.DataNotes.All(line => line is not null);

        if (!valid)
            throw new CoachResponseException("coach_invalid_contract",
                "The coach reply did not match the expected response format.");

        int words = output.Speech.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var (min, max) = mode switch
        {
            "song_debrief" => (30, 60),
            "session_debrief" => (40, 90),
            "chat" => (5, 150),
            _ => throw new InvalidOperationException("Unknown coaching mode.")
        };

        if (words < min || words > max
            || Regex.IsMatch(output.Speech, @"https?://|\[[^\]]*\]",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            throw new CoachResponseException("coach_speech_rejected",
                "The coach reply did not meet the required length or content rules.");

        // Neither current endpoint supplies a series or session calibration facts.
        if (output.FocusSection is not null
            || output.CoachingFocus is "timing_spread" or "density" or "recovery_after_miss" or "fatigue"
                or "calibration_check")
            throw new CoachResponseException("coach_unsupported_facts",
                "The coach reply used analysis that requires unavailable performance data.");
    }
}
