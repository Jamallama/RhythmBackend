using Microsoft.AspNetCore.RateLimiting;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;

namespace RhythmBackend.Sessions;

public static class SessionEndpoints
{
    public static void AddSessionApi(this WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection("SessionApi").Get<SessionApiOptions>() ?? new();
        if (options.LifetimeHours is < 1 or > 24 || options.MaxRunsPerSession is < 1 or > 200
            || options.CleanupIntervalMinutes is < 1 or > 60)
            throw new InvalidOperationException("Session lifetime, quota, or cleanup interval is outside the supported range.");
        if (options.AllowedOrigins.Length == 0 || options.AllowedOrigins.Any(origin =>
            !Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.AbsolutePath != "/"
            || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0
            || !(uri.Scheme == "https" || builder.Environment.IsDevelopment() && uri.IsLoopback)))
            throw new InvalidOperationException("Configure exact browser origins, without repository paths.");
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ISessionObjectStore>(_ => options.StorageProvider switch
        {
            "AzureBlob" => new AzureSessionObjectStore(options, allowLocalEmulator: builder.Environment.IsDevelopment()),
            "Local" when builder.Environment.IsDevelopment() => new LocalSessionObjectStore(
                options.LocalDirectory ?? Path.Combine(builder.Environment.ContentRootPath, "obj", "session-store")),
            _ => throw new InvalidOperationException("Use AzureBlob storage in production; Local is development-only.")
        });
        builder.Services.AddSingleton<SessionService>();
        builder.Services.AddHostedService<SessionCleanupWorker>();
        builder.Services.AddCors(cors => cors.AddPolicy("SessionBrowser", policy => policy
            .WithOrigins(options.AllowedOrigins)
            .WithMethods("GET", "POST", "PUT", "DELETE", "OPTIONS")
            .WithHeaders("Authorization", "Content-Type", "Accept")
            .WithExposedHeaders("Retry-After")));
        builder.Services.AddRateLimiter(rate =>
        {
            rate.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
                ctx.Request.Method == "POST" && ctx.Request.Path == "/v1/sessions"
                    ? RateLimitPartition.GetFixedWindowLimiter("registration-global", _ => new()
                    {
                        PermitLimit = 100, Window = TimeSpan.FromHours(1), QueueLimit = 0
                    })
                    : RateLimitPartition.GetNoLimiter("other")),
                PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
                    ctx.Request.Path.StartsWithSegments("/v1") && ctx.Request.Method is "POST" or "PUT"
                        ? RateLimitPartition.GetConcurrencyLimiter("session-writes", _ => new()
                        {
                            PermitLimit = 8, QueueLimit = 0
                        })
                        : RateLimitPartition.GetNoLimiter("other")));
            rate.AddPolicy("session-register", ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new()
                {
                    PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
                }));
            rate.AddPolicy("session-requests", ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new()
                {
                    PermitLimit = 240, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
                }));
            rate.OnRejected = async (context, ct) =>
            {
                int retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var remaining)
                    ? Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds)) : 60;
                context.HttpContext.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
                await Error(429, "rate_limited", "Request rate limit reached. Retry after the indicated delay.")
                    .ExecuteAsync(context.HttpContext);
            };
        });
    }

    public static void UseSessionApi(this WebApplication app)
    {
        app.Use(async (ctx, next) =>
        {
            bool protectedSurface = ctx.Request.Path.StartsWithSegments("/v1")
                || ctx.Request.Path.StartsWithSegments("/sessions")
                || ctx.Request.Path == "/internal/session-cleanup" || ctx.Request.Path == "/health";
            if (!protectedSurface) { await next(ctx); return; }

            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
            var originalCancellation = ctx.RequestAborted;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(originalCancellation);
            deadline.CancelAfter(TimeSpan.FromMinutes(2));
            ctx.RequestAborted = deadline.Token;
            try
            {
                if (!app.Environment.IsDevelopment() && !ctx.Request.IsHttps)
                    throw new SessionApiException(400, "https_required", "Use the HTTPS API address directly; credentials are never redirected.");
                if (ctx.Request.Path.StartsWithSegments("/v1") && ctx.Request.Query.Keys.Any(
                    key => key.Equals("key", StringComparison.OrdinalIgnoreCase)))
                    throw new SessionApiException(400, "query_key_not_allowed", "Use the Authorization header on API requests.");
                await next(ctx);
            }
            catch (SessionApiException ex) { await Error(ex.Status, ex.Code, ex.Message).ExecuteAsync(ctx); }
            catch (JsonException) { await Error(400, "invalid_json", "Malformed JSON, missing fields, or an invalid UTC timestamp.").ExecuteAsync(ctx); }
            catch (BadHttpRequestException ex)
            {
                await Error(ex.StatusCode == 413 ? 413 : 400, ex.StatusCode == 413 ? "request_too_large" : "invalid_request",
                    "Invalid request or request size exceeds the supported limit.").ExecuteAsync(ctx);
            }
            catch (OperationCanceledException) when (originalCancellation.IsCancellationRequested) { ctx.Response.StatusCode = 499; }
            catch (OperationCanceledException)
            {
                await Error(503, "request_timeout", "Operation timed out. Retry with the same identity and payload.").ExecuteAsync(ctx);
            }
            catch (Exception ex)
            {
                // No exception body/stack here: upstream errors can contain signed
                // URLs, credentials or request payloads. Infrastructure logging is
                // separately addressed in the deployment runbook.
                app.Logger.LogError("Session API failed ({ErrorType}).", ex.GetType().Name);
                await Error(503, "storage_unavailable", "Session storage is unavailable. Retry the same request later.").ExecuteAsync(ctx);
            }
            finally { ctx.RequestAborted = originalCancellation; }
        });
        app.UseRouting();
        app.UseCors("SessionBrowser");
        app.UseRateLimiter();
    }

    public static void MapSessionApi(this WebApplication app)
    {
        var routes = app.MapGroup("/v1").RequireRateLimiting("session-requests");
        routes.MapPost("/sessions", async (HttpContext ctx, SessionService service) =>
        {
            var request = await Body<RegisterSessionRequest>(ctx);
            var (state, created) = await service.RegisterAsync(request, ctx.RequestAborted);
            return Json(new { contract_version = 1, state.SessionId, state.CreatedAt, state.ExpiresAt }, created ? 201 : 200);
        }).RequireRateLimiting("session-register");

        routes.MapPut("/sessions/{session_id}/runs/{run_id}", async (HttpContext ctx, SessionService service, string session_id, string run_id) =>
        {
            // Authenticate before reading a potentially large request body.
            await service.ReadAsync(session_id, Bearer(ctx), upload: true, ctx.RequestAborted);
            SessionService.RequireId(run_id);
            var request = await Body<UploadRunRequest>(ctx);
            var (run, created) = await service.UploadAsync(session_id, run_id, Bearer(ctx), request, ctx.RequestAborted);
            return Json(new { session_id, run.RunId, run.Sequence, status = "completed", run.CompletedAt }, created ? 201 : 200);
        });

        routes.MapGet("/sessions/{session_id}/runs", async (HttpContext ctx, SessionService service, string session_id) =>
        {
            var state = await service.ReadAsync(session_id, Bearer(ctx), upload: false, ctx.RequestAborted);
            long after = Cursor(ctx, "after_sequence", 0, long.MaxValue);
            int limit = (int)Cursor(ctx, "limit", 100, 100);
            if (limit == 0) throw new SessionApiException(400, "invalid_cursor", "Limit must be between 1 and 100.");
            var all = state.Runs.Where(run => run.Sequence > after).OrderBy(run => run.Sequence).ToArray();
            var page = all.Take(limit).ToArray();
            return Json(new
            {
                state.SessionId,
                state.ExpiresAt,
                runs = page.Select(run => new
                {
                    run.RunId, run.Sequence, status = "completed", run.CompletedAt, run.FinishedAtUtc,
                    run.GameId, run.SongId, run.ChartId, run.SampleIntervalSec, run.Autoplay,
                    run.LatencyBasis, run.SuccessfulHitCount, run.SummaryExceedsFormatRange,
                    files = new
                    {
                        series = $"/v1/sessions/{session_id}/runs/{run.RunId}/series.csv",
                        summary = $"/v1/sessions/{session_id}/runs/{run.RunId}/summary.csv"
                    }
                }),
                next_after_sequence = page.LastOrDefault()?.Sequence ?? after,
                has_more = all.Length > page.Length
            });
        });

        routes.MapGet("/sessions/{session_id}/runs/{run_id}/{filename}", async (
            HttpContext ctx, SessionService service, string session_id, string run_id, string filename) =>
        {
            byte[] bytes = await service.DownloadAsync(session_id, run_id, filename, Bearer(ctx), ctx.RequestAborted);
            return Results.File(bytes, "text/csv; charset=utf-8", fileDownloadName: filename, enableRangeProcessing: false);
        });

        routes.MapDelete("/sessions/{session_id}", async (HttpContext ctx, SessionService service, string session_id) =>
        {
            await service.RevokeAsync(session_id, Bearer(ctx), ctx.RequestAborted);
            return Results.NoContent();
        });

        app.MapGet("/health", async (ISessionObjectStore store, CancellationToken ct) =>
        {
            await store.ReadAsync("health/probe", ct);
            return Json(new { status = "ok", session_contract_version = 1 });
        }).RequireRateLimiting("session-requests");

        app.MapGet("/openapi.json", () => Results.File(
            Path.Combine(app.Environment.ContentRootPath, "docs", "session-api.openapi.json"), "application/json"));

        app.MapGet("/sessions/{session_id}/", (HttpContext ctx, SessionApiOptions options, string session_id) =>
        {
            SessionService.RequireId(session_id);
            if (ctx.Request.Query.Count != 1 || ctx.Request.Query["key"].Count != 1 || !SessionService.ValidKey(ctx.Request.Query["key"]))
                return Error(400, "invalid_session_link", "Invalid session link.");
            const string script = "history.replaceState(null,'',location.pathname);";
            string hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(script)));
            ctx.Response.Headers.ContentSecurityPolicy = $"default-src 'none'; script-src 'sha256-{hash}'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'";
            bool enabled = app.Environment.IsDevelopment() || options.QrLoggingRedactionConfirmed;
            string message = enabled
                ? "Scan this session link with the phone client to retrieve completed song attempts."
                : "Session links are not enabled until deployment logging has been configured.";
            return Results.Content($"<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"referrer\" content=\"no-referrer\"><title>Rhythm session</title><script>{script}</script></head><body><h1>Rhythm session</h1><p>{message}</p></body></html>",
                "text/html; charset=utf-8", statusCode: enabled ? 200 : 503);
        }).RequireRateLimiting("session-requests");

        // An external scheduler can wake Free F1 and drive cleanup even when the
        // in-process worker is unloaded. This secret must never enter a game build.
        app.MapPost("/internal/session-cleanup", async (HttpContext ctx, IConfiguration configuration, SessionService service) =>
        {
            string? expected = configuration["SessionApi:MaintenanceKey"];
            string? supplied = Bearer(ctx);
            if (!SessionService.ValidKey(expected) || !SessionService.ValidKey(supplied)
                || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(expected!)),
                    SHA256.HashData(Encoding.UTF8.GetBytes(supplied!))))
                return Error(401, "unauthorized", "A valid maintenance credential is required.");
            await service.CleanupAsync(ctx.RequestAborted);
            return Json(new { status = "completed" });
        }).RequireRateLimiting("session-register");

        app.Map("/v1/{**unmatched}", () => Error(404, "not_found", "API route was not found."));
    }

    private static string? Bearer(HttpContext ctx)
    {
        var headers = ctx.Request.Headers.Authorization;
        if (headers.Count != 1) return null;
        string? value = headers[0];
        return value is not null && value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? value[7..] : null;
    }

    private static async Task<T> Body<T>(HttpContext ctx)
    {
        if (!ctx.Request.HasJsonContentType())
            throw new SessionApiException(400, "invalid_content_type", "Send application/json.");
        if (ctx.Request.ContentLength > RunCsvValidator.MaxRequestBytes)
            throw new SessionApiException(413, "request_too_large", "JSON requests are limited to 2 MiB.");
        using var bytes = new MemoryStream();
        byte[] buffer = new byte[8192];
        int count;
        while ((count = await ctx.Request.Body.ReadAsync(buffer, ctx.RequestAborted)) != 0)
        {
            if (bytes.Length + count > RunCsvValidator.MaxRequestBytes)
                throw new SessionApiException(413, "request_too_large", "JSON requests are limited to 2 MiB.");
            bytes.Write(buffer, 0, count);
        }
        return JsonSerializer.Deserialize<T>(bytes.GetBuffer().AsSpan(0, (int)bytes.Length), SessionJson.Options)
            ?? throw new SessionApiException(400, "invalid_json", "A JSON object is required.");
    }

    private static long Cursor(HttpContext ctx, string name, long defaultValue, long maximum)
    {
        var values = ctx.Request.Query[name];
        if (values.Count == 0) return defaultValue;
        if (values.Count != 1 || !long.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out long value) || value > maximum)
            throw new SessionApiException(400, "invalid_cursor", "Invalid pagination cursor or limit.");
        return value;
    }

    public static IResult Json<T>(T value, int status = 200) => Results.Json(value, SessionJson.Options, statusCode: status);
    public static IResult Error(int status, string code, string message) => Json(new { error = new { code, message } }, status);
}
