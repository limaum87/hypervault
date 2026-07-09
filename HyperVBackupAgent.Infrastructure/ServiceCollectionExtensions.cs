using HyperVBackupAgent.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HyperVBackupAgent.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddHyperVBackupAgent(this IServiceCollection services, IConfiguration configuration)
    {
        var provider = configuration["HyperVBackupAgent:HyperVProvider"] ?? "Simulation";
        var backupRoot = configuration["HyperVBackupAgent:BackupRoot"]
            ?? Path.Combine(AppContext.BaseDirectory, "backups");
        var simulationRoot = configuration["HyperVBackupAgent:SimulationRoot"]
            ?? Path.Combine(AppContext.BaseDirectory, "sim-vms");

        services.AddSingleton<IPowerShellRunner, PowerShellRunner>();
        if (string.Equals(provider, "PowerShell", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IHyperVService, PowerShellHyperVService>();
        }
        else
        {
            services.AddSingleton<IHyperVService>(_ => new SimulatedHyperVService(simulationRoot));
        }

        services.AddSingleton<IRctService, SimulatedRctService>();
        services.AddSingleton<IStorageProvider, FileStorageProvider>();
        services.AddSingleton<IMetadataRepository, JsonMetadataRepository>();
        services.AddSingleton<IRestorePointCatalog>(serviceProvider =>
            new FileSystemRestorePointCatalog(backupRoot, serviceProvider.GetRequiredService<IMetadataRepository>()));
        services.AddSingleton<IRetentionService, FileSystemRetentionService>();
        services.AddSingleton<IHashService, HashService>();
        services.AddSingleton<RestoreMaterializer>();
        services.AddSingleton<VhdReadOnlyMountValidator>();
        services.AddSingleton<IBackupEngine, BackupEngine>();
        services.AddSingleton<IVerifyEngine, VerifyEngine>();
        services.AddSingleton<IRestoreEngine, RestoreEngine>();
        return services;
    }
}
