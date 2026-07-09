# HyperVBackupAgent

HyperVBackupAgent is a lightweight Windows agent for Hyper-V hosts. It is intended for small environments that need scheduled VM backups, incremental backup support through Hyper-V Resilient Change Tracking (RCT), backup-chain verification, and simple restore workflows without a central Windows backup server.

The project targets .NET 8 and is designed to run without a graphical interface. It includes a CLI for local operations, an HTTPS API for future orchestration, and a Windows Service host for scheduled execution.

## Project Status

This is an MVP implementation. The codebase currently includes:

- Hyper-V abstraction with both simulation and PowerShell-backed providers.
- RCT abstraction with both simulation and native/Hyper-V-backed provider selection.
- Full backup flow with temporary Production Checkpoint creation and cleanup.
- Incremental backup flow using changed ranges from the active `IRctService`.
- Backup metadata stored as JSON chain files.
- Chain verification.
- Restore with incremental block application.
- Targeted restore to a specific restore point.
- Restore verification that reconstructs disks into a temporary folder and attempts read-only VHDX mount validation when applicable.
- File-system restore-point catalog for API listing.
- Temporary checkpoint cleanup.
- File-system retention policy.
- Windows Service scheduler for weekly full backups, daily incrementals, and retention.
- Unit tests for simulation-mode backup, restore, verification, catalog, retention, and DI selection.

Native Hyper-V/RCT code compiles and is isolated, but it still requires validation on a real Hyper-V host with administrator permissions.

## Solution Layout

```text
HyperVBackupAgent.Core
  Domain models and interfaces.

HyperVBackupAgent.Infrastructure
  Hyper-V providers, RCT providers, backup engine, restore engine,
  verification, storage, metadata, retention, and PowerShell integration.

HyperVBackupAgent.Cli
  Local command-line tool.

HyperVBackupAgent.Api
  HTTPS API with bearer-token authentication.

HyperVBackupAgent.Service
  Windows Service host and scheduler.

HyperVBackupAgent.Tests
  Unit tests and simulation-mode flow tests.
```

## Requirements

- .NET 8 SDK or runtime.
- Windows for real Hyper-V operations.
- Hyper-V PowerShell module for the PowerShell provider.
- Administrator permissions for checkpoint, VHDX mount, and VM operations.
- Hyper-V VM configuration version that supports RCT for real incremental backups.

Simulation mode can run without Hyper-V.

## Configuration

The main configuration section is `HyperVBackupAgent`.

```json
{
  "HyperVBackupAgent": {
    "ApiToken": "",
    "BackupRoot": "backups",
    "AllowedPathRoots": [],
    "Api": {
      "ConfigureKestrel": false,
      "HttpPort": 5080,
      "HttpsPort": 5443,
      "Certificate": {
        "AutoGenerate": true,
        "Path": "",
        "Password": "",
        "StorePath": "",
        "Subject": "CN=HyperVBackupAgent API",
        "ValidDays": 825
      },
      "Jobs": {
        "StorePath": ""
      },
      "Logging": {
        "FileEnabled": true,
        "Directory": "",
        "RetainedFileCountLimit": 14,
        "FileSizeLimitBytes": 104857600
      }
    },
    "HyperVProvider": "Simulation",
    "RctProvider": "Simulation",
    "SimulationRoot": "sim-vms"
  }
}
```

Provider options:

- `HyperVProvider=Simulation`: reads fake VMs from `SimulationRoot`.
- `HyperVProvider=PowerShell`: uses Hyper-V PowerShell cmdlets.
- `RctProvider=Simulation`: produces simulated changed ranges.
- `RctProvider=Native`: uses `virtdisk.dll` and Hyper-V WMI/CIM calls for RCT.

Environment variables can override configuration. Example:

```powershell
$env:HVB_HyperVBackupAgent__HyperVProvider = "PowerShell"
$env:HVB_HyperVBackupAgent__RctProvider = "Native"
$env:HVB_HyperVBackupAgent__BackupRoot = "D:\HyperVBackups"
```

## Simulation Mode

Create one folder per VM under `SimulationRoot`. Each VM folder can contain `.bin` or `.vhdx` files.

```powershell
$env:HVB_HyperVBackupAgent__SimulationRoot = "C:\sim-vms"
New-Item -ItemType Directory -Force C:\sim-vms\ERP01
Set-Content C:\sim-vms\ERP01\disk-0.bin "fake disk content"
```

Then list VMs:

```powershell
dotnet run --project HyperVBackupAgent.Cli -- list-vms
```

## CLI Usage

```powershell
dotnet run --project HyperVBackupAgent.Cli -- list-vms
dotnet run --project HyperVBackupAgent.Cli -- vm-info --vm "ERP01"

dotnet run --project HyperVBackupAgent.Cli -- backup-full `
  --vm "ERP01" `
  --destination "C:\backup\hyperv"

dotnet run --project HyperVBackupAgent.Cli -- backup-inc `
  --vm "ERP01" `
  --destination "C:\backup\hyperv"

dotnet run --project HyperVBackupAgent.Cli -- verify-chain `
  --chain-id "C:\backup\hyperv\HOST\VMID\chain-20260708-180000"

dotnet run --project HyperVBackupAgent.Cli -- verify-restore `
  --restore-point "C:\backup\hyperv\HOST\VMID\chain-20260708-180000"

dotnet run --project HyperVBackupAgent.Cli -- restore `
  --restore-point "C:\backup\hyperv\HOST\VMID\chain-20260708-180000" `
  --destination "C:\restore" `
  --new-name "ERP01-Restore-Test"

dotnet run --project HyperVBackupAgent.Cli -- restore `
  --restore-point "C:\backup\hyperv\HOST\VMID\chain-20260708-180000" `
  --destination "C:\restore" `
  --new-name "ERP01-Restore-Test" `
  --backup-id "inc-0001"

dotnet run --project HyperVBackupAgent.Cli -- cleanup-temp-checkpoints

dotnet run --project HyperVBackupAgent.Cli -- apply-retention `
  --backup-root "C:\backup\hyperv" `
  --keep-chains 7 `
  --keep-days 30 `
  --dry-run
```

## API

`GET /health` is public. All other endpoints require:

```text
Authorization: Bearer <token>
```

Set `HyperVBackupAgent:ApiToken` before exposing the API.

For production hosting, configure fixed Kestrel endpoints. The default production-friendly path is to let the agent create a local self-signed certificate and register its fingerprint in the central server:

```json
{
  "HyperVBackupAgent": {
    "Api": {
      "ConfigureKestrel": true,
      "HttpPort": 5080,
      "HttpsPort": 5443,
      "Certificate": {
        "AutoGenerate": true,
        "StorePath": "C:\\ProgramData\\HyperVBackupAgent\\certs\\agent-api.pfx",
        "Password": ""
      }
    }
  }
}
```

When `AutoGenerate=true` and `Certificate:Path` is empty, the API creates or reuses a local PFX. On Windows, the default location is `C:\ProgramData\HyperVBackupAgent\certs\agent-api.pfx`. The authenticated endpoint `GET /agent/certificate` returns the SHA-256 fingerprint that the central server should store during enrollment.

If a customer already has a certificate, set `Certificate:Path` and `Certificate:Password` to use that PFX instead. If `AutoGenerate=false` and `Certificate:Path` is empty, Kestrel falls back to the default HTTPS certificate available to the process. Use `HyperVBackupAgent:AllowedPathRoots` to restrict API-supplied backup, restore, and verification paths.

Implemented endpoints:

- `GET /health`
- `GET /health/live`
- `GET /health/ready`
- `GET /agent`
- `GET /agent/certificate`
- `GET /configuration/effective`
- `GET /jobs`
- `GET /jobs/{id}`
- `POST /jobs/{id}/cancel`
- `POST /jobs/backup-full`
- `POST /jobs/backup-incremental`
- `POST /jobs/verify-chain`
- `POST /jobs/verify-restore`
- `POST /jobs/restore`
- `GET /vms`
- `GET /vms/{id}`
- `GET /vms/{id}/restore-points`
- `GET /vms/{id}/restore-points?status=Completed&from=2026-07-01T00:00:00Z&to=2026-07-31T23:59:59Z`
- `POST /backups/preflight`
- `POST /restore/preflight`
- `POST /backups/full`
- `POST /backups/incremental`
- `POST /backups/verify-chain`
- `POST /backups/verify-restore`
- `POST /restore`
- `POST /maintenance/cleanup-temp-checkpoints`
- `POST /maintenance/apply-retention`

The `/jobs/*` endpoints are the preferred integration path for a central server because backup, restore, and verification can take a long time. Job history is stored locally as JSON. On Windows, the default path is `C:\ProgramData\HyperVBackupAgent\jobs\api-jobs.json`; override it with `HyperVBackupAgent:Api:Jobs:StorePath`.

Use pre-flight endpoints before creating long-running jobs. Backup pre-flight validates VM existence, Production Checkpoint support, destination accessibility, free space, and RCT availability for incrementals. Restore pre-flight validates the chain metadata, destination, free space, and VM name conflicts.

API logs are structured JSON on stdout and, by default, daily rolling files. On Windows, file logs are written to `C:\ProgramData\HyperVBackupAgent\logs\hypervbackupagent-api-*.log`; override the directory with `HyperVBackupAgent:Api:Logging:Directory`, retention with `RetainedFileCountLimit`, and per-file size with `FileSizeLimitBytes`. Each HTTP response includes `X-Correlation-Id`; clients may also supply that header. Request logs and job logs include `CorrelationId`, and asynchronous job execution logs include `JobId`.

Health checks are public. `GET /health/live` reports whether the API process is running. `GET /health/ready` returns `200` only when the API token is configured, the backup root exists and is accessible, and the configured Hyper-V provider can be initialized; otherwise it returns `503` with per-check details.

## Windows Services

Production deployments should install two automatic Windows services:

- `HyperVBackupAgent.Api` keeps the local HTTPS API online for central orchestration, health checks, job creation, restore, and diagnostics.
- `HyperVBackupAgent.Scheduler` runs the automatic backup schedule and retention policy.

The services are separated so the API can remain available even if scheduled backup execution needs to restart or be diagnosed independently. Both services should be configured with `StartupType=Automatic`.

The repository includes a service installation helper that expects this publish layout:

```text
artifacts/publish/
  api/
    HyperVBackupAgent.Api.exe
  scheduler/
    HyperVBackupAgent.Service.exe
```

Run from an elevated PowerShell session after publishing:

```powershell
.\scripts\install-windows-services.ps1 -PublishRoot .\artifacts\publish
```

## Scheduler

The scheduler is enabled by default for automatic backups. Configure its schedule under:

```json
{
  "HyperVBackupAgent": {
    "Scheduler": {
      "Enabled": true,
      "BackupRoot": "D:\\HyperVBackups",
      "VmNames": [ "ERP01", "DC01" ],
      "PollInterval": "00:01:00",
      "DailyIncrementalTime": "22:00:00",
      "WeeklyFullDay": "Sunday",
      "WeeklyFullTime": "01:00:00",
      "ApplyRetentionAfterBackup": true,
      "KeepLastChains": 7,
      "KeepDays": 30
    }
  }
}
```

If `VmNames` is empty, the service lists all VMs through the configured Hyper-V provider.

## Backup Format

Backups are stored under:

```text
{backup-root}/
  {host}/
    {vm-id}/
      chain-{yyyyMMdd-HHmmss}/
        chain.json
        full/
          disk-0.full.vhdx
          metadata.json
        increments/
          inc-0001/
            inc.json
            disk-0.blocks
```

`chain.json` stores VM identity, source host, restore points, disk metadata, hashes, RCT references, retention policy, and chain status.

Incremental `.blocks` files contain changed block ranges and raw block data. Restore rebuilds from the full backup and applies increments in order up to the target restore point.

## RCT Notes

`IRctService` has two implementations:

- `SimulatedRctService`: deterministic simulation for local tests.
- `NativeHyperVRctService`: native/Hyper-V path using `virtdisk.dll` and `Msvm_ImageManagementService.GetVirtualDiskChanges`.

Native RCT should be validated on a real Hyper-V host. Expected failure modes include missing administrator permissions, unsupported VM/disk configuration, RCT disabled, or expired change tracking IDs.

## Verification

`verify-chain` checks:

- `chain.json` readability.
- Full backup presence.
- File existence.
- Hashes.
- Increment parent ordering.

`verify-restore` checks the chain, reconstructs disks into a temporary directory, verifies that disk files were produced, and attempts read-only VHDX/AVHDX mount validation when applicable.

## Retention

The retention service supports:

- Keeping the last N chains.
- Removing chains older than N days.
- Protecting the latest valid full chain for each VM.
- Skipping incomplete chains and returning a warning.
- Dry-run mode.

## Development

Build and test:

```powershell
dotnet build HyperVBackupAgent.sln
dotnet test HyperVBackupAgent.sln --no-build
```

Current test coverage is focused on simulation-mode behavior and composition. Real Hyper-V integration tests should be run separately on a Hyper-V host.

## Known Limitations

- Native RCT has not yet been validated against a real Hyper-V host in this repository session.
- SQLite is referenced but not yet used as the primary metadata index.
- VM restore currently creates a basic VM configuration; it does not fully recreate CPU, memory, firmware, network, or advanced VM settings.
- No compression, encryption, deduplication, cloud storage, or granular file restore.
- No Hyper-V cluster support.
