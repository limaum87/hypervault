using HyperVBackupAgent.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HyperVBackupAgent.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddHyperVBackupAgent(this IServiceCollection services, IConfiguration configuration)
    {
        var simulationRoot = configuration["HyperVBackupAgent:SimulationRoot"]
            ?? Path.Combine(AppContext.BaseDirectory, "sim-vms");

        services.AddSingleton<IHyperVService>(_ => new SimulatedHyperVService(simulationRoot));
        services.AddSingleton<IRctService, SimulatedRctService>();
        services.AddSingleton<IStorageProvider, FileStorageProvider>();
        services.AddSingleton<IMetadataRepository, JsonMetadataRepository>();
        services.AddSingleton<IHashService, HashService>();
        services.AddSingleton<IBackupEngine, BackupEngine>();
        services.AddSingleton<IVerifyEngine, VerifyEngine>();
        services.AddSingleton<IRestoreEngine, RestoreEngine>();
        return services;
    }
}
