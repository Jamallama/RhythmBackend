
using RhythmBackend.Services;
using RhythmBackend.Sessions;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<GeminiService>();
builder.AddSessionApi();

var app = builder.Build();
app.UseSessionApi();
app.MapSessionApi();

// Test whether the backend is running
app.MapGet("/", () =>
{
    return Results.Ok(new
    {
        message = "Rhythm Backend is running!"
    });
});

// Receive results from Unity or Mentra CV
app.MapPost("/api/results", async (
    PlayResult result,
    GeminiService gemini,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(result.SessionId))
        return Results.BadRequest("Session ID is required.");

    if (result.Score < 0 || result.Score > 1_000_000)
        return Results.BadRequest("Invalid score.");

    if (!double.IsFinite(result.Accuracy) || result.Accuracy is < 0 or > 100)
        return Results.BadRequest("Invalid accuracy.");

    if (result.Bpm <= 0)
        return Results.BadRequest("BPM must be greater than zero.");

    if (result.TotalNoteCount <= 0)
        return Results.BadRequest("Total note count must be greater than zero.");

    if (result.GameTimeMs < 0)
        return Results.BadRequest("Game time cannot be negative.");

    if (!double.IsFinite(result.AverageLatencyMs))
        return Results.BadRequest("Invalid average latency.");

    try
    {
        app.Logger.LogInformation("Song results received; waiting for Gemini coaching.");
        CoachResponse coach = await gemini.CoachAsync(result, cancellationToken);
        app.Logger.LogInformation("Gemini coaching received; returning song results.");

        return Results.Ok(new
        {
            message = "Results received successfully!",
            sessionId = result.SessionId,
            result,
            coaching = coach.Speech,
            coach
        });
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        app.Logger.LogInformation("Results request canceled by the caller.");
        return Results.StatusCode(499);
    }
    catch (OperationCanceledException)
    {
        app.Logger.LogWarning("Gemini coaching timed out after 45 seconds.");
        return Results.Problem(
            "Gemini did not respond within 45 seconds. Please try again.",
            statusCode: 504);
    }
    catch (CoachResponseException ex)
    {
        app.Logger.LogWarning("Coach reply rejected: {Code}", ex.Code);
        return Results.Problem(ex.Message, statusCode: 502,
            extensions: new Dictionary<string, object?> { ["errorCode"] = ex.Code });
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning("Gemini coaching failed: {Error}", ex.Message);
        return Results.Problem("Unable to generate coaching. Please try again.", statusCode: 502);
    }
});

app.MapPost("/api/chat", async (
    ChatRequest request,
    GeminiService gemini,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Message))
    {
        return Results.BadRequest(
            "Message cannot be empty.");
    }

    if (request.Message.Length > 2000)
    {
        return Results.BadRequest(
            "Message is too long.");
    }

    try
    {
        app.Logger.LogInformation("Chat request received; waiting for Gemini.");
        CoachResponse coach = await gemini.AskAsync(
            request.Message, cancellationToken);

        app.Logger.LogInformation("Gemini reply received; returning response.");

        return Results.Ok(new
        {
            reply = coach.Speech,
            coach
        });
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        app.Logger.LogInformation("Chat request canceled by the caller.");
        return Results.StatusCode(499);
    }
    catch (OperationCanceledException)
    {
        app.Logger.LogWarning("Gemini request timed out after 45 seconds.");
        return Results.Problem(
            "Gemini did not respond within 45 seconds. Please try again.",
            statusCode: 504);
    }
    catch (CoachResponseException ex)
    {
        app.Logger.LogWarning("Coach reply rejected: {Code}", ex.Code);
        return Results.Problem(ex.Message, statusCode: 502,
            extensions: new Dictionary<string, object?> { ["errorCode"] = ex.Code });
    }
    catch (Exception ex)
    {
        Console.WriteLine(
            $"Gemini request failed: {ex.Message}");

        return Results.Problem(
            "Unable to contact Gemini.",
            statusCode: 502);
    }
});

app.Run();

// Data structure for incoming song results
public record PlayResult(
    string SessionId,
    int GameId,
    int SongId,
    int Difficulty,
    long GameTimeMs,
    int Bpm,
    int TotalNoteCount,
    int Score,
    double Accuracy,
    double AverageLatencyMs
);

public record ChatRequest(string Message);
