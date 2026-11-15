using System.Text.RegularExpressions;

namespace RhythmBackend.Services;

public static class CoachResponseValidator
{
    // Mechanical checks from the supplied package. Semantic grounding still depends
    // on the system prompt; these checks do not establish scientific evidence.
    public static void Validate(CoachResponse output, string mode, string? tier)
    {
        bool valid = output.Mode == mode && output.Tier == tier
            && output.CoachingFocus is "judgement_quality" or "combo_continuity" or "recovery_after_miss"
                or "density_or_difficulty" or "consistency" or "lane_execution" or "timing_bias"
                or "timing_spread" or "calibration_check" or "progression" or "data_quality" or "none"
            && output.EvidenceBasis is "measured_run_data" or "cross_run_pattern" or "game_heuristic"
                or "practice_convention" or "mixed_evidence" or "none"
            && output.Observations is not null && output.Observations.Length <= 3
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
            "song_debrief" => (30, 120),
            "session_debrief" => (60, 180),
            "chat" => (5, 150),
            _ => throw new InvalidOperationException("Unknown coaching mode.")
        };

        if (words < min || words > max
            || Regex.IsMatch(output.Speech, @"https?://|\[[^\]]*\]",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            throw new CoachResponseException("coach_speech_rejected",
                "The coach reply did not meet the required length or content rules.");

        // Neither current endpoint supplies a series or session calibration facts.
        bool unsupportedForTier = tier != "A" && (output.CoachingFocus is "timing_spread" or "recovery_after_miss" or "density_or_difficulty")
            || output.CoachingFocus is "calibration_check" && tier == "A";
        if (tier != "A" && output.FocusSection is not null || unsupportedForTier)
            throw new CoachResponseException("coach_unsupported_facts",
                "The coach reply used analysis that requires unavailable performance data.");
    }
}
