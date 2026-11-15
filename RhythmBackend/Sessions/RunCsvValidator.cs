using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RhythmBackend.Sessions;

public static class RunCsvValidator
{
    public const int MaxCsvBytes = 256 * 1024;
    public const int MaxRequestBytes = 2 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static void Validate(UploadRunRequest run)
    {
        if (run.ContractVersion != 1) Invalid("unsupported_contract_version", "Only contract_version 1 is supported.");
        if (run.GameId < 0 || run.SongId < 0 || run.ChartId < 0 || run.SampleIntervalSec is not (1 or 2)
            || run.SuccessfulHitCount < 0 || run.LatencyBasis != "successful_hits_only_v1")
            Invalid("invalid_metadata", "Invalid identity, sampling interval, successful hit count, or latency basis.");

        CheckSize(run.SeriesCsv);
        CheckSize(run.SummaryCsv);
        var series = Lines(run.SeriesCsv);
        var summary = Lines(run.SummaryCsv);
        if (series.Count < 6 || summary.Count != 1) Invalid("invalid_csv", "Expected series metadata and header, and exactly one summary record.");

        string[] names = ["game_id", "song_id", "chart_id", "bpm", "sample_interval_sec"];
        var metadata = new string[5];
        for (int i = 0; i < names.Length; i++)
        {
            var cells = series[i].Split(',');
            if (cells.Length != 2 || cells[0] != names[i]) Invalid("invalid_csv", "Series metadata rows are missing or out of order.");
            metadata[i] = cells[1];
        }
        if (Integer(metadata[0]) != run.GameId || Integer(metadata[1]) != run.SongId
            || Integer(metadata[2]) != run.ChartId || Integer(metadata[4]) != run.SampleIntervalSec)
            Invalid("metadata_mismatch", "Series identity or sampling interval differs from the upload envelope.");
        double nominalBpm = Number(metadata[3]);
        if (nominalBpm < 0) Invalid("invalid_csv", "BPM cannot be negative.");
        if (series[5] != "game_time,percent_accuracy,avg_latency,bpm,nps,latency_spread")
            Invalid("invalid_csv", "Unexpected series header.");

        var fields = summary[0].Split(',');
        if (fields.Length != 8) Invalid("invalid_csv", "Summary must have eight fields and no header.");
        var values = fields.Select(Integer).ToArray();
        if (values[0] is < 0 or > 99 || values[1] is < 0 or > 999 || values[2] is < 1 or > 4
            || values[3] is < 0 or > 999999 || values[4] is < 0 or > 999
            || values[5] is < 0 or > 3000 || values[6] is < 0 or > 1_000_000)
            Invalid("invalid_csv", "Summary fields exceed the documented format ranges.");

        long expectedGame = run.SummaryExceedsFormatRange ? Math.Min(run.GameId, 99) : run.GameId;
        long expectedSong = run.SummaryExceedsFormatRange ? Math.Min(run.SongId, 999) : run.SongId;
        double expectedBpm = run.SummaryExceedsFormatRange ? Math.Min(nominalBpm, 999) : nominalBpm;
        if (values[0] != expectedGame || values[1] != expectedSong || Math.Abs(values[4] - expectedBpm) > 0.500001)
            Invalid("metadata_mismatch", "Summary identity or rounded BPM differs from series/envelope values beyond permitted clamps.");

        long notes = 0;
        int windowsWithTiming = 0;
        long maximumTimingHits = 0;
        for (int i = 6; i < series.Count; i++)
        {
            var row = series[i].Split(',');
            if (row.Length != 6) Invalid("invalid_csv", "Each series record must have six fields.");
            if (Integer(row[0]) != (long)(i - 6) * run.SampleIntervalSec)
                Invalid("invalid_csv", "Series windows must start at zero and follow the declared sampling interval.");
            double? accuracy = OptionalNumber(row[1]);
            double? latency = OptionalNumber(row[2]);
            double bpm = Number(row[3]);
            long count = Integer(row[4]);
            double? spread = OptionalNumber(row[5]);
            if (accuracy is < 0 or > 100 || bpm < 0 || count < 0 || spread < 0)
                Invalid("invalid_csv", "Series accuracy, BPM, count, or spread is outside its valid range.");
            try { notes = checked(notes + count); }
            catch (OverflowException) { Invalid("invalid_csv", "Series note count exceeds supported range."); }
            if (count == 0 && (accuracy.HasValue || latency.HasValue || spread.HasValue))
                Invalid("invalid_csv", "A window without notes cannot contain judgement measurements.");
            if (latency.HasValue)
            {
                windowsWithTiming++;
                maximumTimingHits += count;
            }
            if (spread.HasValue && (!latency.HasValue || count < 2 || run.SuccessfulHitCount < 2))
                Invalid("invalid_csv", "Timing spread requires at least two successful hits and an available mean.");
            if (run.SuccessfulHitCount == 0 && (latency.HasValue || spread.HasValue))
                Invalid("invalid_csv", "Zero successful hits requires unavailable series latency and spread.");
        }

        long expectedNotes = run.SummaryExceedsFormatRange ? Math.Min(notes, 3000) : notes;
        if (values[5] != expectedNotes)
            Invalid("metadata_mismatch", "Series note count does not match the summary, including permitted saturation.");
        if (run.SuccessfulHitCount > notes || run.SuccessfulHitCount < windowsWithTiming
            || run.SuccessfulHitCount > maximumTimingHits)
            Invalid("invalid_csv", "Successful hit count is inconsistent with available series timing windows.");
        if (run.SuccessfulHitCount == 0 && values[7] != 0)
            Invalid("invalid_csv", "Zero successful hits requires the summary's documented zero-latency sentinel.");
    }

    public static string Digest(UploadRunRequest run)
    {
        // Explicit field order and byte-length delimiters: independent of JSON property
        // order/escaping, while CSV CRLF vs LF remains a real content difference.
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string value)
        {
            byte[] bytes = Utf8.GetBytes(value);
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            digest.AppendData(length);
            digest.AppendData(bytes);
        }
        Add(run.ContractVersion.ToString(CultureInfo.InvariantCulture));
        Add(run.FinishedAtUtc.UtcTicks.ToString(CultureInfo.InvariantCulture));
        Add(run.GameId.ToString(CultureInfo.InvariantCulture));
        Add(run.SongId.ToString(CultureInfo.InvariantCulture));
        Add(run.ChartId.ToString(CultureInfo.InvariantCulture));
        Add(run.SampleIntervalSec.ToString(CultureInfo.InvariantCulture));
        Add(run.Autoplay ? "1" : "0");
        Add(run.LatencyBasis);
        Add(run.SuccessfulHitCount.ToString(CultureInfo.InvariantCulture));
        Add(run.SummaryExceedsFormatRange ? "1" : "0");
        Add(run.SeriesCsv);
        Add(run.SummaryCsv);
        return Convert.ToHexString(digest.GetHashAndReset());
    }

    private static void CheckSize(string? csv)
    {
        if (string.IsNullOrEmpty(csv)) Invalid("invalid_csv", "Both complete CSV strings are required.");
        try
        {
            if (Utf8.GetByteCount(csv!) > MaxCsvBytes)
                throw new SessionApiException(413, "csv_too_large", "Each CSV is limited to 256 KiB of UTF-8.");
        }
        catch (EncoderFallbackException) { Invalid("invalid_csv", "CSV must be valid UTF-8 text."); }
    }

    private static List<string> Lines(string csv)
    {
        using var reader = new StringReader(csv);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);
        // ReadLine accepts LF/CRLF and a single final line ending, without
        // trimming the original uploaded bytes or silently dropping blank rows.
        return lines;
    }

    private static long Integer(string text)
    {
        if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long value))
            Invalid("invalid_csv", "Expected an integer CSV field.");
        return value;
    }

    private static double Number(string text)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value))
            Invalid("invalid_csv", "Expected a finite numeric CSV field.");
        return value;
    }

    private static double? OptionalNumber(string text) => string.IsNullOrWhiteSpace(text) ? null : Number(text);
    private static void Invalid(string code, string message) => throw new SessionApiException(422, code, message);
}
