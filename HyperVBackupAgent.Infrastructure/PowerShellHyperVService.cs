using System.Text.Json;
using HyperVBackupAgent.Core;

namespace HyperVBackupAgent.Infrastructure;

public sealed class PowerShellHyperVService : IHyperVService
{
    private readonly IPowerShellRunner _powerShell;

    public PowerShellHyperVService(IPowerShellRunner powerShell)
    {
        _powerShell = powerShell;
    }

    public async Task<IReadOnlyList<VirtualMachineInfo>> ListVmsAsync(CancellationToken cancellationToken = default)
    {
        var json = await RunJsonAsync("""
            $ErrorActionPreference = 'Stop'
            Get-VM | ForEach-Object {
              $vm = $_
              $disks = @(Get-VMHardDiskDrive -VMName $vm.Name | ForEach-Object {
                $drive = $_
                $vhd = $null
                if ($drive.Path -and (Test-Path -LiteralPath $drive.Path)) {
                  $vhd = Get-VHD -Path $drive.Path
                }
                [pscustomobject]@{
                  Id = $drive.ControllerType.ToString() + ':' + $drive.ControllerNumber + ':' + $drive.ControllerLocation
                  Path = $drive.Path
                  VirtualSizeBytes = if ($vhd) { [int64]$vhd.Size } else { [int64]0 }
                  PhysicalSizeBytes = if ($vhd) { [int64]$vhd.FileSize } elseif ($drive.Path -and (Test-Path -LiteralPath $drive.Path)) { [int64](Get-Item -LiteralPath $drive.Path).Length } else { [int64]0 }
                }
              })
              $checkpoints = @(Get-VMSnapshot -VMName $vm.Name -ErrorAction SilentlyContinue | ForEach-Object {
                [pscustomobject]@{
                  Id = $_.Id.ToString()
                  Name = $_.Name
                  CreatedAt = $_.CreationTime.ToUniversalTime().ToString('O')
                  IsProduction = ($_.CheckpointType -match 'Production')
                }
              })
              [pscustomobject]@{
                Id = $vm.Id.ToString()
                Name = $vm.Name
                State = $vm.State.ToString()
                Generation = [int]$vm.Generation
                MemoryBytes = [int64]$vm.MemoryAssigned
                Disks = $disks
                Checkpoints = $checkpoints
              }
            } | ConvertTo-Json -Depth 8
            """, cancellationToken);

        return DeserializeList(json);
    }

    public async Task<VirtualMachineInfo?> GetVmAsync(string nameOrId, CancellationToken cancellationToken = default)
    {
        var vms = await ListVmsAsync(cancellationToken);
        return vms.FirstOrDefault(vm =>
            string.Equals(vm.Name, nameOrId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(vm.Id, nameOrId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<bool> SupportsProductionCheckpointAsync(string vmId, CancellationToken cancellationToken = default)
    {
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $vm = Get-VM | Where-Object { $_.Id.ToString() -eq '{{Escape(vmId)}}' -or $_.Name -eq '{{Escape(vmId)}}' } | Select-Object -First 1
            if (-not $vm) { throw 'VM not found: {{Escape(vmId)}}' }
            [pscustomobject]@{
              Supported = ($vm.CheckpointType.ToString() -eq 'Production' -or $vm.CheckpointType.ToString() -eq 'ProductionOnly')
            } | ConvertTo-Json
            """;
        var json = await RunJsonAsync(script, cancellationToken);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("Supported").GetBoolean();
    }

    public async Task<string> CreateProductionCheckpointAsync(string vmId, string name, CancellationToken cancellationToken = default)
    {
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $vm = Get-VM | Where-Object { $_.Id.ToString() -eq '{{Escape(vmId)}}' -or $_.Name -eq '{{Escape(vmId)}}' } | Select-Object -First 1
            if (-not $vm) { throw 'VM not found: {{Escape(vmId)}}' }
            $originalCheckpointType = $vm.CheckpointType
            if ($vm.CheckpointType.ToString() -ne 'Production' -and $vm.CheckpointType.ToString() -ne 'ProductionOnly') {
              Set-VM -Name $vm.Name -CheckpointType Production
            }
            Checkpoint-VM -Name $vm.Name -SnapshotName '{{Escape(name)}}'
            if ($originalCheckpointType -ne $vm.CheckpointType) {
              Set-VM -Name $vm.Name -CheckpointType $originalCheckpointType
            }
            $snapshot = Get-VMSnapshot -VMName $vm.Name -Name '{{Escape(name)}}'
            [pscustomobject]@{ Id = $snapshot.Id.ToString(); Name = $snapshot.Name } | ConvertTo-Json
            """;
        var json = await RunJsonAsync(script, cancellationToken);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("Id").GetString() ?? name;
    }

    public async Task<IReadOnlyList<VirtualDiskInfo>> GetCheckpointConsistentDisksAsync(string vmId, string checkpointId, CancellationToken cancellationToken = default)
    {
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $vm = Get-VM | Where-Object { $_.Id.ToString() -eq '{{Escape(vmId)}}' -or $_.Name -eq '{{Escape(vmId)}}' } | Select-Object -First 1
            if (-not $vm) { throw 'VM not found: {{Escape(vmId)}}' }
            $snapshot = Get-VMSnapshot -VMName $vm.Name | Where-Object { $_.Id.ToString() -eq '{{Escape(checkpointId)}}' -or $_.Name -eq '{{Escape(checkpointId)}}' } | Select-Object -First 1
            if (-not $snapshot) { throw 'Checkpoint not found: {{Escape(checkpointId)}}' }
            Get-VMHardDiskDrive -VMName $vm.Name | ForEach-Object {
              $drive = $_
              $readPath = $drive.Path
              $vhd = $null
              if ($drive.Path -and (Test-Path -LiteralPath $drive.Path)) {
                $vhd = Get-VHD -Path $drive.Path
                if ($vhd.ParentPath -and (Test-Path -LiteralPath $vhd.ParentPath)) {
                  $readPath = $vhd.ParentPath
                  $vhd = Get-VHD -Path $readPath
                }
              }
              [pscustomobject]@{
                Id = $drive.ControllerType.ToString() + ':' + $drive.ControllerNumber + ':' + $drive.ControllerLocation
                Path = $readPath
                VirtualSizeBytes = if ($vhd) { [int64]$vhd.Size } else { [int64]0 }
                PhysicalSizeBytes = if ($vhd) { [int64]$vhd.FileSize } elseif ($readPath -and (Test-Path -LiteralPath $readPath)) { [int64](Get-Item -LiteralPath $readPath).Length } else { [int64]0 }
              }
            } | ConvertTo-Json -Depth 5
            """;
        var json = await RunJsonAsync(script, cancellationToken);
        return DeserializeDiskList(json);
    }

    public async Task RemoveCheckpointAsync(string vmId, string checkpointId, CancellationToken cancellationToken = default)
    {
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $vm = Get-VM | Where-Object { $_.Id.ToString() -eq '{{Escape(vmId)}}' -or $_.Name -eq '{{Escape(vmId)}}' } | Select-Object -First 1
            if (-not $vm) { throw 'VM not found: {{Escape(vmId)}}' }
            $snapshot = Get-VMSnapshot -VMName $vm.Name | Where-Object { $_.Id.ToString() -eq '{{Escape(checkpointId)}}' -or $_.Name -eq '{{Escape(checkpointId)}}' } | Select-Object -First 1
            if ($snapshot) {
              Remove-VMSnapshot -VMName $vm.Name -Name $snapshot.Name
            }
            [pscustomobject]@{ Removed = $snapshot -ne $null } | ConvertTo-Json
            """;
        await RunJsonAsync(script, cancellationToken);
    }

    public async Task<IReadOnlyList<CheckpointCleanupResult>> CleanupTemporaryCheckpointsAsync(string namePrefix = "HyperVBackupAgent-", CancellationToken cancellationToken = default)
    {
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $results = @()
            Get-VM | ForEach-Object {
              $vm = $_
              Get-VMSnapshot -VMName $vm.Name -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -like '{{Escape(namePrefix)}}*' } |
                ForEach-Object {
                  $snapshot = $_
                  try {
                    Remove-VMSnapshot -VMName $vm.Name -Name $snapshot.Name -ErrorAction Stop
                    $results += [pscustomobject]@{
                      VmId = $vm.Id.ToString()
                      VmName = $vm.Name
                      CheckpointId = $snapshot.Id.ToString()
                      CheckpointName = $snapshot.Name
                      Removed = $true
                      Error = $null
                    }
                  } catch {
                    $results += [pscustomobject]@{
                      VmId = $vm.Id.ToString()
                      VmName = $vm.Name
                      CheckpointId = $snapshot.Id.ToString()
                      CheckpointName = $snapshot.Name
                      Removed = $false
                      Error = $_.Exception.Message
                    }
                  }
                }
            }
            $results | ConvertTo-Json -Depth 5
            """;
        var json = await RunJsonAsync(script, cancellationToken);
        return DeserializeCleanupResults(json);
    }

    public async Task CreateVmFromDisksAsync(string vmName, IReadOnlyList<string> diskPaths, bool overwriteExisting, CancellationToken cancellationToken = default)
    {
        if (diskPaths.Count == 0)
        {
            throw new ArgumentException("At least one disk is required to create a restored VM.", nameof(diskPaths));
        }

        var diskArray = string.Join(", ", diskPaths.Select(path => $"'{Escape(path)}'"));
        var overwrite = overwriteExisting ? "$true" : "$false";
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $vmName = '{{Escape(vmName)}}'
            $overwrite = {{overwrite}}
            $disks = @({{diskArray}})
            $existing = Get-VM -Name $vmName -ErrorAction SilentlyContinue
            if ($existing -and -not $overwrite) {
              throw "VM already exists: $vmName"
            }
            if ($existing -and $overwrite) {
              Stop-VM -Name $vmName -TurnOff -Force -ErrorAction SilentlyContinue
              Remove-VM -Name $vmName -Force
            }
            New-VM -Name $vmName -Generation 2 -MemoryStartupBytes 1GB -VHDPath $disks[0] | Out-Null
            for ($i = 1; $i -lt $disks.Count; $i++) {
              Add-VMHardDiskDrive -VMName $vmName -Path $disks[$i]
            }
            [pscustomobject]@{ Created = $true; Name = $vmName } | ConvertTo-Json
            """;
        await RunJsonAsync(script, cancellationToken);
    }

    private async Task<string> RunJsonAsync(string script, CancellationToken cancellationToken)
    {
        var result = await _powerShell.RunAsync(script, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Hyper-V PowerShell command failed: {result.StandardError.Trim()}");
        }

        return result.StandardOutput.Trim();
    }

    private static IReadOnlyList<VirtualMachineInfo> DeserializeList(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        if (json.TrimStart().StartsWith('['))
        {
            return JsonSerializer.Deserialize<List<VirtualMachineInfo>>(json, options) ?? [];
        }

        var single = JsonSerializer.Deserialize<VirtualMachineInfo>(json, options);
        return single is null ? [] : [single];
    }

    private static IReadOnlyList<VirtualDiskInfo> DeserializeDiskList(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        if (json.TrimStart().StartsWith('['))
        {
            return JsonSerializer.Deserialize<List<VirtualDiskInfo>>(json, options) ?? [];
        }

        var single = JsonSerializer.Deserialize<VirtualDiskInfo>(json, options);
        return single is null ? [] : [single];
    }

    private static IReadOnlyList<CheckpointCleanupResult> DeserializeCleanupResults(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        if (json.TrimStart().StartsWith('['))
        {
            return JsonSerializer.Deserialize<List<CheckpointCleanupResult>>(json, options) ?? [];
        }

        var single = JsonSerializer.Deserialize<CheckpointCleanupResult>(json, options);
        return single is null ? [] : [single];
    }

    private static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}
