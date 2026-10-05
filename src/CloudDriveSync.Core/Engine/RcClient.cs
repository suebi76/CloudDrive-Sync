using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using CloudDriveSync.Core.Errors;

namespace CloudDriveSync.Core.Engine;

/// <summary>Progress of a running engine job (bytes and files so far, of how many).</summary>
public sealed record JobProgress(long Bytes, long TotalBytes, long Transfers, long TotalTransfers, double BytesPerSecond, long Checks, long TotalChecks, long Errors)
{
    public static JobProgress None { get; } = new(0, 0, 0, 0, 0, 0, 0, 0);
}

/// <summary>Result of an engine job: success, rclone's error text and the job's output.</summary>
public sealed record JobResult(bool Success, string Error, JsonObject Output);

/// <summary>
/// Client of rclone's remote control API (the engine). Only 127.0.0.1, never through a proxy, with the random
/// credentials of this engine start.
/// </summary>
public sealed class RcClient : IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);
    private readonly HttpClient _http;

    public RcClient(int port, string user, string password)
    {
        Port = port;
        _http = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
    }

    public int Port { get; }

    /// <summary>Calls one command; rclone's errors become <see cref="CdException"/> with a classified code.</summary>
    public async Task<JsonObject> CallAsync(string command, JsonObject? body = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var limit = timeout ?? DefaultTimeout;
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timer.CancelAfter(limit);
        HttpResponseMessage response;
        string text;
        try
        {
            using var content = new StringContent((body ?? new JsonObject()).ToJsonString(), Encoding.UTF8, "application/json");
            response = await _http.PostAsync(command, content, timer.Token);
            text = await response.Content.ReadAsStringAsync(timer.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CdException("CD-5003", $"{command}: no answer within {limit.TotalSeconds:0} s");
        }
        catch (HttpRequestException e)
        {
            throw new CdException("CD-5003", $"{command}: {e.Message}", e);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode) return ParseObject(text);
            if (response.StatusCode == HttpStatusCode.Unauthorized) throw new CdException("CD-5003", $"{command}: unauthorized");
            var error = ParseObject(text)["error"]?.GetValue<string>() ?? text;
            throw new CdException(ErrorCatalog.Classify(error), $"{command}: {error}");
        }
    }

    /// <summary>
    /// Runs a command as an asynchronous engine job and waits for it. <paramref name="progress"/> gets the job's
    /// statistics about twice a second; cancelling stops the job.
    /// </summary>
    public async Task<JobResult> RunJobAsync(string command, JsonObject body, string group, Action<JobProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        body["_async"] = true;
        body["_group"] = group;
        var started = await CallAsync(command, body, cancellationToken: cancellationToken);
        var jobId = started["jobid"]?.GetValue<long>() ?? throw new CdException("CD-9000", $"{command}: no job id");
        try
        {
            while (true)
            {
                var status = await CallAsync("job/status", new JsonObject { ["jobid"] = jobId }, cancellationToken: cancellationToken);
                if (status["finished"]?.GetValue<bool>() == true)
                {
                    var success = status["success"]?.GetValue<bool>() == true;
                    var error = status["error"]?.GetValue<string>() ?? "";
                    var output = status["output"] as JsonObject ?? new JsonObject();
                    return new JobResult(success, error, (JsonObject)output.DeepClone());
                }
                if (progress is not null)
                {
                    var stats = await CallAsync("core/stats", new JsonObject { ["group"] = group }, cancellationToken: cancellationToken);
                    progress(ParseProgress(stats));
                }
                await Task.Delay(500, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            await StopJobAsync(jobId);
            throw;
        }
    }

    private async Task StopJobAsync(long jobId)
    {
        try
        {
            await CallAsync("job/stop", new JsonObject { ["jobid"] = jobId }, TimeSpan.FromSeconds(10));
            for (var i = 0; i < 40; i++)
            {
                var status = await CallAsync("job/status", new JsonObject { ["jobid"] = jobId }, TimeSpan.FromSeconds(10));
                if (status["finished"]?.GetValue<bool>() == true) return;
                await Task.Delay(250);
            }
        }
        catch (CdException)
        {
            // The engine may already be gone; nothing to stop then.
        }
    }

    internal static JobProgress ParseProgress(JsonObject stats)
    {
        static long Long(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<double>(out var d) ? (long)d : 0;
        static double Double(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;
        return new JobProgress(Long(stats, "bytes"), Long(stats, "totalBytes"), Long(stats, "transfers"), Long(stats, "totalTransfers"),
            Double(stats, "speed"), Long(stats, "checks"), Long(stats, "totalChecks"), Long(stats, "errors"));
    }

    private static JsonObject ParseObject(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        try
        {
            return JsonNode.Parse(text) as JsonObject ?? new JsonObject();
        }
        catch (System.Text.Json.JsonException)
        {
            return new JsonObject { ["error"] = text };
        }
    }

    public void Dispose() => _http.Dispose();
}
