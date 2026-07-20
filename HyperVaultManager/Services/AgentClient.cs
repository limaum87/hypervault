using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyperVaultManager.Models;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace HyperVaultManager.Services;

/// <summary>Server-side client for the HyperVBackupAgent API running on each host.</summary>
public class AgentClient
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly IHttpClientFactory _factory;
    private readonly ILogger<AgentClient> _logger;

    public AgentClient(IHttpClientFactory factory, ILogger<AgentClient> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public static string BuildBaseUrl(HyperVHost host) =>
        $"{(host.UseHttps ? "https" : "http")}://{host.IpOrFqdn}:{host.Port}";

    public string NewCorrelationId() => Guid.NewGuid().ToString("N");

    public async Task<AgentHealthResult> GetHealthAsync(HyperVHost host, CancellationToken ct = default)
    {
        var client = CreateClient();
        var baseUrl = BuildBaseUrl(host);
        var live = await GetJsonAsync<JsonObject>($"{baseUrl}/health/live", host, client, ct).ConfigureAwait(false);
        var ready = await GetJsonAsync<JsonObject>($"{baseUrl}/health/ready", host, client, ct).ConfigureAwait(false);

        var liveStatus = live?["status"]?.ToString() ?? "unknown";
        var readyStatus = ready?["status"]?.ToString() ?? "unknown";
        return new AgentHealthResult(liveStatus, readyStatus, readyStatus == "ready");
    }

    public async Task<JsonObject?> GetAgentInfoAsync(HyperVHost host, CancellationToken ct = default)
        => await GetJsonAsync<JsonObject>($"{BuildBaseUrl(host)}/agent", host, CreateClient(), ct).ConfigureAwait(false);

    public async Task<JsonArray?> GetRestorePointsAsync(HyperVHost host, string vmExternalId, CancellationToken ct = default)
        => await GetJsonAsync<JsonArray>($"{BuildBaseUrl(host)}/vms/{Uri.EscapeDataString(vmExternalId)}/restore-points", host, CreateClient(), ct).ConfigureAwait(false);

    public async Task<List<VmSnapshot>> GetVmsAsync(HyperVHost host, CancellationToken ct = default)
    {
        var arr = await GetJsonAsync<JsonArray>($"{BuildBaseUrl(host)}/vms", host, CreateClient(), ct).ConfigureAwait(false);
        var result = new List<VmSnapshot>();
        if (arr is null) return result;
        foreach (var item in arr)
        {
            if (item is not JsonObject o) continue;
            long disk = 0;
            if (o["disks"] is JsonArray disks)
            {
                foreach (var d in disks.OfType<JsonObject>())
                    disk += d["virtualSizeBytes"]?.GetValue<long>() ?? d["physicalSizeBytes"]?.GetValue<long>() ?? 0;
            }
            result.Add(new VmSnapshot(
                o["id"]?.ToString() ?? string.Empty,
                o["name"]?.ToString() ?? string.Empty,
                o["state"]?.ToString() ?? "Unknown",
                (int)(o["generation"]?.GetValue<long>() ?? 0),
                o["memoryBytes"]?.GetValue<long>() ?? 0,
                disk));
        }
        return result;
    }

    public async Task<JsonObject?> PreflightBackupAsync(HyperVHost host, string vmNameOrId, string destination, CancellationToken ct = default)
    {
        var body = new { vmNameOrId, destination };
        return await PostJsonAsync<JsonObject>($"{BuildBaseUrl(host)}/backups/preflight", host, body, ct).ConfigureAwait(false);
    }

    public async Task<AgentJob> EnqueueBackupAsync(HyperVHost host, string type, string vmNameOrId, string destination, CancellationToken ct = default)
    {
        var endpoint = type == JobTypes.Incremental ? "/jobs/backup-incremental" : "/jobs/backup-full";
        var body = new { vmNameOrId, destination };
        return await PostJobAsync($"{BuildBaseUrl(host)}{endpoint}", host, body, ct).ConfigureAwait(false);
    }

    public async Task<AgentJob> EnqueueVerifyChainAsync(HyperVHost host, string chainPath, CancellationToken ct = default)
    {
        var body = new { chainPath };
        return await PostJobAsync($"{BuildBaseUrl(host)}/jobs/verify-chain", host, body, ct).ConfigureAwait(false);
    }

    public async Task<AgentJob> EnqueueVerifyRestoreAsync(HyperVHost host, string restorePointPath, CancellationToken ct = default)
    {
        var body = new { restorePointPath, keepTemporaryFiles = false };
        return await PostJobAsync($"{BuildBaseUrl(host)}/jobs/verify-restore", host, body, ct).ConfigureAwait(false);
    }

    public async Task<AgentJob> EnqueueRestoreAsync(HyperVHost host, RestoreRequestPayload payload, CancellationToken ct = default)
    {
        return await PostJobAsync($"{BuildBaseUrl(host)}/jobs/restore", host, payload, ct).ConfigureAwait(false);
    }

    public async Task<JsonObject?> GetJobAsync(HyperVHost host, string jobId, CancellationToken ct = default)
        => await GetJsonAsync<JsonObject>($"{BuildBaseUrl(host)}/jobs/{Uri.EscapeDataString(jobId)}", host, CreateClient(), ct).ConfigureAwait(false);

    public async Task CancelJobAsync(HyperVHost host, string jobId, CancellationToken ct = default)
    {
        var client = CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Post, $"{BuildBaseUrl(host)}/jobs/{Uri.EscapeDataString(jobId)}/cancel");
        ApplyAuth(req, host);
        using var resp = await SendAsync(client, req, host, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    // ---- helpers ----
    private HttpClient CreateClient() => _factory.CreateClient("agent");

    private static void ApplyAuth(HttpRequestMessage req, HyperVHost host)
    {
        if (!string.IsNullOrWhiteSpace(host.ApiToken))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", host.ApiToken);
        req.Headers.Add("X-Correlation-Id", Guid.NewGuid().ToString("N"));
    }

    /// <summary>Sends a request and translates transport-level failures into a clean
    /// <see cref="AgentCallException"/> (timeout, connection refused, DNS, etc.).</summary>
    private async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage req, HyperVHost host, CancellationToken ct)
    {
        try
        {
            return await client.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new AgentCallException(0, $"Agent '{host.Name}' is unreachable: {ex.Message}");
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException || ct.IsCancellationRequested is false)
        {
            throw new AgentCallException(0, $"Agent '{host.Name}' timed out or is unreachable.");
        }
    }

    private async Task<T?> GetJsonAsync<T>(string url, HyperVHost host, HttpClient client, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyAuth(req, host);
        using var resp = await SendAsync(client, req, host, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(resp, host).ConfigureAwait(false);
        return await resp.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false);
    }

    private async Task<T?> PostJsonAsync<T>(string url, HyperVHost host, object body, CancellationToken ct)
    {
        var client = CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        ApplyAuth(req, host);
        req.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
        using var resp = await SendAsync(client, req, host, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(resp, host).ConfigureAwait(false);
        return await resp.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false);
    }

    private async Task<AgentJob> PostJobAsync(string url, HyperVHost host, object body, CancellationToken ct)
    {
        var client = CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        ApplyAuth(req, host);
        req.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
        using var resp = await SendAsync(client, req, host, ct).ConfigureAwait(false);
        // The agent returns 202 Accepted with the job object.
        var json = await resp.Content.ReadFromJsonAsync<JsonObject>(Json, ct).ConfigureAwait(false);
        return AgentJob.From(json);
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage resp, HyperVHost host)
    {
        if (resp.IsSuccessStatusCode) return;
        string detail;
        try { detail = await resp.Content.ReadAsStringAsync(ct_default).ConfigureAwait(false); }
        catch { detail = string.Empty; }
        throw new AgentCallException((int)resp.StatusCode, $"Agent {host.Name} returned {((int)resp.StatusCode)}: {Truncate(detail, 500)}");
    }

    private static readonly CancellationToken ct_default = CancellationToken.None;
    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "...";

    /// <summary>Maps the agent's job JSON to a stable status string we store in the DB.</summary>
    public static string NormalizeJobStatus(JsonObject? job)
    {
        if (job is null) return RunStatuses.Running;
        var raw = job["status"];
        // numeric: 0=Queued,1=Running,2=Succeeded,3=Failed,4=Canceled
        if (raw is JsonValue v && v.TryGetValue<int>(out var num))
        {
            return num switch
            {
                0 => RunStatuses.Queued,
                1 => RunStatuses.Running,
                2 => RunStatuses.Succeeded,
                3 => RunStatuses.Failed,
                4 => RunStatuses.Canceled,
                _ => RunStatuses.Running
            };
        }
        var text = raw?.ToString().Trim();
        return text?.ToLowerInvariant() switch
        {
            "queued" or "pending" => RunStatuses.Queued,
            "running" => RunStatuses.Running,
            "succeeded" or "completed" or "ok" or "success" => RunStatuses.Succeeded,
            "failed" or "error" => RunStatuses.Failed,
            "canceled" or "cancelled" => RunStatuses.Canceled,
            _ => RunStatuses.Running
        };
    }
}

public sealed record AgentHealthResult(string LiveStatus, string ReadyStatus, bool IsReady);

public sealed record VmSnapshot(string Id, string Name, string State, int Generation, long MemoryBytes, long DiskSizeBytes);

public sealed record RestoreRequestPayload(
    string RestorePoint, string Destination, string NewName, bool OverwriteExisting, string? TargetBackupId, bool CreateVm = true);

public sealed record AgentJob(string JobId, string? Type, string Status, string? ResultPath, string? Error, string? Message)
{
    public static AgentJob From(JsonObject? json)
    {
        if (json is null) throw new AgentCallException(0, "Agent did not return a job.");
        var id = json["jobId"]?.ToString();
        if (string.IsNullOrWhiteSpace(id)) id = json["id"]?.ToString();
        if (string.IsNullOrWhiteSpace(id)) throw new AgentCallException(0, "Agent job response missing jobId.");
        var outcome = json["outcome"] as JsonObject;
        return new AgentJob(
            id,
            json["type"]?.ToString(),
            AgentClient.NormalizeJobStatus(json),
            json["resultPath"]?.ToString() ?? outcome?["path"]?.ToString() ?? json["path"]?.ToString(),
            json["error"]?.ToString(),
            json["message"]?.ToString() ?? outcome?["message"]?.ToString());
    }
}

public sealed class AgentCallException : Exception
{
    public int StatusCode { get; }
    public AgentCallException(int statusCode, string message) : base(message) => StatusCode = statusCode;
}
