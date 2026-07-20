using HyperVBackupAgent.Core;

namespace HyperVBackupAgent.Api;

public sealed class ApiFileLevelRestoreCleanupWorker : BackgroundService
{
    private readonly IFileLevelRestoreService _sessions;
    private readonly ILogger<ApiFileLevelRestoreCleanupWorker> _logger;

    public ApiFileLevelRestoreCleanupWorker(IFileLevelRestoreService sessions, ILogger<ApiFileLevelRestoreCleanupWorker> logger)
    {
        _sessions = sessions;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _sessions.CleanupOrphanedSessionsAsync(stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Could not clean up orphaned file-level restore sessions at startup.");
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await _sessions.CleanupExpiredSessionsAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Could not clean up expired file-level restore sessions.");
            }
        }
    }
}
