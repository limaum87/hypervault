using System.Text.Json;
using HyperVBackupAgent.Api;
using Microsoft.Extensions.Configuration;

namespace HyperVBackupAgent.Tests;

public sealed class ApiAgentInfoServiceTests
{
    [Fact]
    public void GetAgentInfoReturnsInventoryFields()
    {
        var service = new ApiAgentInfoService(CreateConfiguration());

        var info = service.GetAgentInfo();

        Assert.False(string.IsNullOrWhiteSpace(info.Version));
        Assert.False(string.IsNullOrWhiteSpace(info.Hostname));
        Assert.Equal("PowerShell", info.HyperVProvider);
        Assert.Equal("Native", info.RctProvider);
        Assert.True(info.SchedulerEnabled);
    }

    [Fact]
    public void GetEffectiveConfigurationDoesNotExposeSecrets()
    {
        var service = new ApiAgentInfoService(CreateConfiguration());

        var configuration = service.GetEffectiveConfiguration();
        var json = JsonSerializer.Serialize(configuration);

        Assert.DoesNotContain("super-secret-token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pfx-password", json, StringComparison.OrdinalIgnoreCase);
        Assert.True(configuration.Certificate.HasConfiguredPath);
        Assert.True(configuration.Certificate.HasConfiguredStorePath);
        Assert.True(configuration.Logging.FileEnabled);
        Assert.True(configuration.Logging.HasConfiguredDirectory);
        Assert.Equal(21, configuration.Logging.RetainedFileCountLimit);
        Assert.Equal(123456789, configuration.Logging.FileSizeLimitBytes);
        Assert.Equal(5443, configuration.Api.HttpsPort);
        Assert.Equal(7, configuration.Scheduler.KeepLastChains);
    }

    private static IConfiguration CreateConfiguration()
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HyperVBackupAgent:ApiToken"] = "super-secret-token",
                ["HyperVBackupAgent:BackupRoot"] = "D:\\Backups",
                ["HyperVBackupAgent:AllowedPathRoots:0"] = "D:\\Backups",
                ["HyperVBackupAgent:SimulationRoot"] = "C:\\Sim",
                ["HyperVBackupAgent:HyperVProvider"] = "PowerShell",
                ["HyperVBackupAgent:RctProvider"] = "Native",
                ["HyperVBackupAgent:Api:ConfigureKestrel"] = "true",
                ["HyperVBackupAgent:Api:HttpPort"] = "5080",
                ["HyperVBackupAgent:Api:HttpsPort"] = "5443",
                ["HyperVBackupAgent:Api:Certificate:AutoGenerate"] = "true",
                ["HyperVBackupAgent:Api:Certificate:Path"] = "C:\\certs\\agent.pfx",
                ["HyperVBackupAgent:Api:Certificate:Password"] = "pfx-password",
                ["HyperVBackupAgent:Api:Certificate:StorePath"] = "C:\\ProgramData\\HyperVBackupAgent\\certs\\agent-api.pfx",
                ["HyperVBackupAgent:Api:Certificate:Subject"] = "CN=HyperVBackupAgent API",
                ["HyperVBackupAgent:Api:Certificate:ValidDays"] = "825",
                ["HyperVBackupAgent:Api:Jobs:StorePath"] = "C:\\ProgramData\\HyperVBackupAgent\\jobs\\api-jobs.json",
                ["HyperVBackupAgent:Api:Logging:FileEnabled"] = "true",
                ["HyperVBackupAgent:Api:Logging:Directory"] = "C:\\ProgramData\\HyperVBackupAgent\\logs",
                ["HyperVBackupAgent:Api:Logging:RetainedFileCountLimit"] = "21",
                ["HyperVBackupAgent:Api:Logging:FileSizeLimitBytes"] = "123456789",
                ["HyperVBackupAgent:Scheduler:Enabled"] = "true",
                ["HyperVBackupAgent:Scheduler:VmNames:0"] = "ERP01",
                ["HyperVBackupAgent:Scheduler:KeepLastChains"] = "7",
                ["HyperVBackupAgent:Scheduler:KeepDays"] = "30"
            })
            .Build();
}
