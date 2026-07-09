using System.Reflection;
using System.Runtime.InteropServices;

namespace HyperVBackupAgent.Api;

public sealed class ApiAgentInfoService
{
    private readonly IConfiguration _configuration;

    public ApiAgentInfoService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public AgentInfo GetAgentInfo()
        => new(
            GetVersion(),
            Environment.MachineName,
            RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture.ToString(),
            _configuration["HyperVBackupAgent:HyperVProvider"] ?? "Simulation",
            _configuration["HyperVBackupAgent:RctProvider"] ?? "Simulation",
            _configuration["HyperVBackupAgent:BackupRoot"] ?? "backups",
            _configuration.GetValue("HyperVBackupAgent:Scheduler:Enabled", false),
            DateTimeOffset.UtcNow);

    public EffectiveConfiguration GetEffectiveConfiguration()
    {
        var apiSection = _configuration.GetSection("HyperVBackupAgent:Api");
        var certificateSection = apiSection.GetSection("Certificate");
        var jobsSection = apiSection.GetSection("Jobs");
        var loggingSection = apiSection.GetSection("Logging");
        var schedulerSection = _configuration.GetSection("HyperVBackupAgent:Scheduler");

        return new EffectiveConfiguration(
            _configuration["HyperVBackupAgent:BackupRoot"] ?? "backups",
            _configuration.GetSection("HyperVBackupAgent:AllowedPathRoots").Get<string[]>() ?? [],
            _configuration["HyperVBackupAgent:SimulationRoot"] ?? "sim-vms",
            _configuration["HyperVBackupAgent:HyperVProvider"] ?? "Simulation",
            _configuration["HyperVBackupAgent:RctProvider"] ?? "Simulation",
            new ApiEndpointOptions(
                apiSection.GetValue("ConfigureKestrel", false),
                apiSection.GetValue<int?>("HttpPort"),
                apiSection.GetValue<int?>("HttpsPort") ?? 5443),
            new ApiCertificateOptions(
                certificateSection.GetValue("AutoGenerate", true),
                !string.IsNullOrWhiteSpace(certificateSection["Path"]),
                !string.IsNullOrWhiteSpace(certificateSection["StorePath"]),
                certificateSection["Subject"] ?? "CN=HyperVBackupAgent API",
                certificateSection.GetValue("ValidDays", 825)),
            new ApiJobsOptions(!string.IsNullOrWhiteSpace(jobsSection["StorePath"])),
            new ApiLoggingOptions(
                loggingSection.GetValue("FileEnabled", true),
                !string.IsNullOrWhiteSpace(loggingSection["Directory"]),
                loggingSection.GetValue<int?>("RetainedFileCountLimit") ?? 14,
                loggingSection.GetValue<long?>("FileSizeLimitBytes") ?? 104_857_600),
            new SchedulerEffectiveOptions(
                schedulerSection.GetValue("Enabled", false),
                schedulerSection["BackupRoot"] ?? _configuration["HyperVBackupAgent:BackupRoot"] ?? "backups",
                schedulerSection.GetSection("VmNames").Get<string[]>() ?? [],
                schedulerSection["PollInterval"] ?? "00:01:00",
                schedulerSection["DailyIncrementalTime"] ?? "22:00:00",
                schedulerSection["WeeklyFullDay"] ?? "Sunday",
                schedulerSection["WeeklyFullTime"] ?? "01:00:00",
                schedulerSection.GetValue("ApplyRetentionAfterBackup", true),
                schedulerSection.GetValue("KeepLastChains", 7),
                schedulerSection.GetValue<int?>("KeepDays") ?? 30));
    }

    private static string GetVersion()
    {
        var assembly = typeof(ApiAgentInfoService).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
    }
}

public sealed record AgentInfo(
    string Version,
    string Hostname,
    string OperatingSystem,
    string Architecture,
    string HyperVProvider,
    string RctProvider,
    string BackupRoot,
    bool SchedulerEnabled,
    DateTimeOffset ServerTimeUtc);

public sealed record EffectiveConfiguration(
    string BackupRoot,
    IReadOnlyList<string> AllowedPathRoots,
    string SimulationRoot,
    string HyperVProvider,
    string RctProvider,
    ApiEndpointOptions Api,
    ApiCertificateOptions Certificate,
    ApiJobsOptions Jobs,
    ApiLoggingOptions Logging,
    SchedulerEffectiveOptions Scheduler);

public sealed record ApiEndpointOptions(
    bool ConfigureKestrel,
    int? HttpPort,
    int HttpsPort);

public sealed record ApiCertificateOptions(
    bool AutoGenerate,
    bool HasConfiguredPath,
    bool HasConfiguredStorePath,
    string Subject,
    int ValidDays);

public sealed record ApiJobsOptions(bool HasConfiguredStorePath);

public sealed record ApiLoggingOptions(
    bool FileEnabled,
    bool HasConfiguredDirectory,
    int RetainedFileCountLimit,
    long FileSizeLimitBytes);

public sealed record SchedulerEffectiveOptions(
    bool Enabled,
    string BackupRoot,
    IReadOnlyList<string> VmNames,
    string PollInterval,
    string DailyIncrementalTime,
    string WeeklyFullDay,
    string WeeklyFullTime,
    bool ApplyRetentionAfterBackup,
    int KeepLastChains,
    int KeepDays);
