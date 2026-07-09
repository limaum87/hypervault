using HyperVBackupAgent.Core;
using HyperVBackupAgent.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

var configuration = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables("HVB_")
    .Build();

var services = new ServiceCollection()
    .AddSingleton<IConfiguration>(configuration)
    .AddHyperVBackupAgent(configuration)
    .BuildServiceProvider();

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    PrintHelp();
    return 0;
}

try
{
    return args[0] switch
    {
        "list-vms" => await ListVmsAsync(),
        "vm-info" => await VmInfoAsync(Required(args, "--vm")),
        "backup-full" => await BackupFullAsync(Required(args, "--vm"), Required(args, "--destination")),
        "backup-inc" => await BackupIncrementalAsync(Required(args, "--vm"), Required(args, "--destination")),
        "verify-chain" => await VerifyChainAsync(Required(args, "--chain-id")),
        "verify-restore" => await VerifyRestoreAsync(Required(args, "--restore-point")),
        "restore" => await RestoreAsync(Required(args, "--restore-point"), Required(args, "--destination"), Required(args, "--new-name"), HasFlag(args, "--overwrite")),
        "cleanup-temp-checkpoints" => NotImplemented("cleanup-temp-checkpoints"),
        "list-restore-points" => await ListRestorePointsAsync(Required(args, "--chain-id")),
        _ => Unknown(args[0])
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

async Task<int> ListVmsAsync()
{
    var hyperV = services.GetRequiredService<IHyperVService>();
    foreach (var vm in await hyperV.ListVmsAsync())
    {
        Console.WriteLine($"{vm.Id}\t{vm.Name}\t{vm.State}\tdisks={vm.Disks.Count}");
    }

    return 0;
}

async Task<int> VmInfoAsync(string vmName)
{
    var hyperV = services.GetRequiredService<IHyperVService>();
    var vm = await hyperV.GetVmAsync(vmName);
    if (vm is null)
    {
        Console.Error.WriteLine($"VM not found: {vmName}");
        return 1;
    }

    Console.WriteLine($"{vm.Name} ({vm.Id})");
    Console.WriteLine($"state={vm.State} generation={vm.Generation} memory={vm.MemoryBytes}");
    foreach (var disk in vm.Disks)
    {
        Console.WriteLine($"disk {disk.Id}: {disk.Path} virtual={disk.VirtualSizeBytes} physical={disk.PhysicalSizeBytes}");
    }

    return 0;
}

async Task<int> BackupFullAsync(string vmName, string destination)
{
    var result = await services.GetRequiredService<IBackupEngine>().RunFullBackupAsync(new BackupRequest(vmName, destination));
    Console.WriteLine($"{result.Status}: {result.BackupId} chain={result.ChainId} path={result.Path}");
    if (result.Error is not null)
    {
        Console.Error.WriteLine(result.Error);
    }

    return result.Status == BackupStatus.Completed ? 0 : 1;
}

async Task<int> BackupIncrementalAsync(string vmName, string destination)
{
    var result = await services.GetRequiredService<IBackupEngine>().RunIncrementalBackupAsync(new BackupRequest(vmName, destination));
    Console.WriteLine($"{result.Status}: {result.BackupId} chain={result.ChainId} path={result.Path}");
    if (result.Error is not null)
    {
        Console.Error.WriteLine(result.Error);
    }

    return result.Status == BackupStatus.Completed ? 0 : 1;
}

async Task<int> VerifyChainAsync(string chainPath)
{
    var result = await services.GetRequiredService<IVerifyEngine>().VerifyChainAsync(chainPath);
    PrintVerify(result);
    return result.IsValid ? 0 : 1;
}

async Task<int> VerifyRestoreAsync(string restorePointPath)
{
    var result = await services.GetRequiredService<IVerifyEngine>().VerifyRestoreAsync(restorePointPath, keepTemporaryFiles: false);
    PrintVerify(result);
    return result.IsValid ? 0 : 1;
}

async Task<int> RestoreAsync(string restorePoint, string destination, string newName, bool overwrite)
{
    await services.GetRequiredService<IRestoreEngine>().RestoreAsync(new RestoreRequest(restorePoint, destination, newName, overwrite));
    Console.WriteLine($"Restore materialized at {Path.GetFullPath(destination)} as {newName}.");
    return 0;
}

async Task<int> ListRestorePointsAsync(string chainPath)
{
    var chain = await services.GetRequiredService<IMetadataRepository>().LoadChainAsync(chainPath);
    foreach (var point in chain.RestorePoints)
    {
        Console.WriteLine($"{point.BackupId}\t{point.Type}\t{point.CreatedAt:O}\t{point.Status}");
    }

    return 0;
}

static string Required(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    if (index < 0 || index + 1 >= args.Length)
    {
        throw new ArgumentException($"Missing required argument {name}.");
    }

    return args[index + 1];
}

static bool HasFlag(string[] args, string name) => args.Contains(name, StringComparer.OrdinalIgnoreCase);

static int Unknown(string command)
{
    Console.Error.WriteLine($"Unknown command: {command}");
    PrintHelp();
    return 1;
}

static int NotImplemented(string command)
{
    Console.Error.WriteLine($"Command '{command}' is declared but not implemented yet.");
    return 2;
}

static void PrintVerify(VerifyResult result)
{
    Console.WriteLine(result.IsValid ? "valid" : "invalid");
    foreach (var error in result.Errors)
    {
        Console.WriteLine($"ERROR {error}");
    }

    foreach (var warning in result.Warnings)
    {
        Console.WriteLine($"WARN {warning}");
    }
}

static void PrintHelp()
{
    Console.WriteLine("""
    hvbackup-agent commands:
      list-vms
      vm-info --vm "ERP01"
      backup-full --vm "ERP01" --destination "C:\backup\hyperv"
      backup-inc --vm "ERP01" --destination "C:\backup\hyperv"
      verify-chain --chain-id "C:\backup\host\vm\chain-..."
      verify-restore --restore-point "C:\backup\host\vm\chain-..."
      restore --restore-point "C:\backup\host\vm\chain-..." --destination "C:\restore" --new-name "ERP01-Restore-Test" [--overwrite]
      list-restore-points --chain-id "C:\backup\host\vm\chain-..."
    """);
}
