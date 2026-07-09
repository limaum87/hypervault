param(
    [string]$PublishRoot = (Join-Path $PSScriptRoot "..\artifacts\publish"),
    [string]$ApiServiceName = "HyperVBackupAgent.Api",
    [string]$SchedulerServiceName = "HyperVBackupAgent.Scheduler"
)

$ErrorActionPreference = "Stop"

function Install-HyperVaultService {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [string]$DisplayName,

        [Parameter(Mandatory = $true)]
        [string]$Description,

        [Parameter(Mandatory = $true)]
        [string]$ExecutablePath
    )

    $resolvedPath = (Resolve-Path -LiteralPath $ExecutablePath).Path
    $existing = Get-Service -Name $Name -ErrorAction SilentlyContinue

    if ($existing) {
        Set-Service -Name $Name -StartupType Automatic -Description $Description
        & sc.exe config $Name binPath= "`"$resolvedPath`"" start= auto DisplayName= "$DisplayName" | Write-Host
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to update service: $Name"
        }

        Write-Host "Updated service: $Name"
        return
    }

    New-Service `
        -Name $Name `
        -DisplayName $DisplayName `
        -Description $Description `
        -BinaryPathName "`"$resolvedPath`"" `
        -StartupType Automatic

    Write-Host "Installed service: $Name"
}

$apiExe = Join-Path $PublishRoot "api\HyperVBackupAgent.Api.exe"
$schedulerExe = Join-Path $PublishRoot "scheduler\HyperVBackupAgent.Service.exe"

Install-HyperVaultService `
    -Name $ApiServiceName `
    -DisplayName "HyperV Backup Agent API" `
    -Description "Local HTTPS API for HyperV Backup Agent orchestration." `
    -ExecutablePath $apiExe

Install-HyperVaultService `
    -Name $SchedulerServiceName `
    -DisplayName "HyperV Backup Agent Scheduler" `
    -Description "Automatic full and incremental backup scheduler for HyperV Backup Agent." `
    -ExecutablePath $schedulerExe

Start-Service -Name $ApiServiceName
Start-Service -Name $SchedulerServiceName

Write-Host "HyperV Backup Agent services are installed and started."
