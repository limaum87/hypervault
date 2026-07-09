param(
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64",
    [string]$ProductVersion = "1.0.0"
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$publishRoot = Join-Path $repoRoot "artifacts\publish"
$apiPublishDir = Join-Path $publishRoot "api"
$schedulerPublishDir = Join-Path $publishRoot "scheduler"
$installerProject = Join-Path $repoRoot "installer\HyperVBackupAgent.Installer\HyperVBackupAgent.Installer.wixproj"

dotnet publish (Join-Path $repoRoot "HyperVBackupAgent.Api\HyperVBackupAgent.Api.csproj") `
    -c $Configuration `
    -r $RuntimeIdentifier `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $apiPublishDir
if ($LASTEXITCODE -ne 0) {
    throw "API publish failed."
}

dotnet publish (Join-Path $repoRoot "HyperVBackupAgent.Service\HyperVBackupAgent.Service.csproj") `
    -c $Configuration `
    -r $RuntimeIdentifier `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $schedulerPublishDir
if ($LASTEXITCODE -ne 0) {
    throw "Scheduler publish failed."
}

dotnet build $installerProject `
    -c $Configuration `
    -p:ProductVersion=$ProductVersion `
    -p:ApiPublishDir=$apiPublishDir `
    -p:SchedulerPublishDir=$schedulerPublishDir
if ($LASTEXITCODE -ne 0) {
    throw "MSI build failed."
}

Write-Host "MSI output:"
Get-ChildItem -Path (Join-Path (Split-Path $installerProject) "bin") -Recurse -Filter "*.msi" |
    Select-Object -ExpandProperty FullName
