using HyperVaultManager.Data;
using HyperVaultManager.Models;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace HyperVaultManager.Services;

/// <summary>Periodically checks host health and updates status.</summary>
public class HostHealthWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly AgentClient _agent;
    private readonly ILogger<HostHealthWorker> _logger;
    private readonly TimeSpan _tick;

    public HostHealthWorker(IServiceScopeFactory scopes, AgentClient agent, ILogger<HostHealthWorker> logger, IConfiguration cfg)
    {
        _scopes = scopes; _agent = agent; _logger = logger;
        _tick = TimeSpan.FromSeconds(cfg.GetValue("Manager:HealthCheckIntervalSeconds", 60));
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        _logger.LogInformation("HostHealthWorker started");
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
                var hosts = await db.Hosts.ToListAsync(stop);
                foreach (var host in hosts)
                {
                    try
                    {
                        var health = await _agent.GetHealthAsync(host, stop);
                        host.Status = health.IsReady ? HostStatuses.Online : HostStatuses.Offline;
                        host.LastSeenAt = DateTimeOffset.UtcNow;
                        if (host.Status == HostStatuses.Online)
                        {
                            try
                            {
                                var info = await _agent.GetAgentInfoAsync(host, stop);
                                host.AgentVersion = info?["version"]?.ToString() ?? host.AgentVersion;
                            }
                            catch { /* non-fatal */ }
                        }
                    }
                    catch (Exception ex)
                    {
                        host.Status = HostStatuses.Offline;
                        _logger.LogDebug(ex, "Health check failed for host {Name}", host.Name);
                    }
                }
                await db.SaveChangesAsync(stop);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _logger.LogError(ex, "Health worker tick failed"); }
            try { await Task.Delay(_tick, stop); }
            catch (OperationCanceledException) { throw; }
        }
        _logger.LogInformation("HostHealthWorker stopped");
    }
}
