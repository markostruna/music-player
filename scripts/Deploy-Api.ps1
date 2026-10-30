<#
.SYNOPSIS
    Deploys the MusicPlayer API binaries to the server using NSSM (stop service, copy, start service).
.DESCRIPTION
    Without -SourcePath the API is published from backend\MusicPlayer.Api first.
    The API applies EF Core migrations automatically at startup. With -Migrate the deployment
    additionally backs up binaries and the SQLite database, waits for the health endpoint
    (i.e. migrations succeeded) and rolls back both if the API does not come up.
.EXAMPLE
    .\Deploy-Api.ps1 -InstallPath D:\Apps\MusicPlayerApi
.EXAMPLE
    .\Deploy-Api.ps1 -InstallPath D:\Apps\MusicPlayerApi -Migrate
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InstallPath,
    [string]$SourcePath,
    [string]$ServiceName = 'MusicPlayerApi',
    [string]$NssmPath = 'nssm.exe',
    [string]$HealthUrl = 'http://127.0.0.1:5080/api/health',
    [int]$HealthTimeoutSeconds = 60,
    # Database-changing release: backup, verify migration, roll back on failure.
    [switch]$Migrate,
    [int]$KeepBackups = 5
)

$ErrorActionPreference = 'Stop'
$InstallPath = [IO.Path]::GetFullPath($InstallPath)
$repoRoot = Split-Path $PSScriptRoot -Parent
$dataDir = Join-Path $InstallPath 'data'
$backupRoot = Join-Path $InstallPath 'backups'
$stamp = Get-Date -Format yyyyMMdd-HHmmss
$publishDir = $null

function Invoke-Nssm { & $NssmPath @args; if ($LASTEXITCODE -ne 0) { throw "nssm $($args -join ' ') failed ($LASTEXITCODE)" } }

function Stop-Api {
    try { & $NssmPath stop $ServiceName 2>&1 | Out-Null } catch { }
    (Get-Service $ServiceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
}

function Copy-Binaries($from) {
    # Mirror binaries but keep runtime state (database, logs, backups).
    robocopy $from $InstallPath /MIR /NFL /NDL /NJH /NJS /NP /XD data logs backups | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }
}

function Wait-Healthy {
    $deadline = (Get-Date).AddSeconds($HealthTimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            if ((Invoke-WebRequest $HealthUrl -UseBasicParsing -TimeoutSec 5).StatusCode -eq 200) { return $true }
        } catch { }
        Start-Sleep -Seconds 2
    }
    return $false
}

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated (Administrator) PowerShell.'
}
if (-not (Get-Command $NssmPath -ErrorAction SilentlyContinue)) { throw "nssm not found ('$NssmPath')." }
if (-not (Get-Service $ServiceName -ErrorAction SilentlyContinue)) {
    throw "Service '$ServiceName' not found. Run Configure-Api.ps1 first."
}

try {
    if (-not $SourcePath) {
        $publishDir = Join-Path ([IO.Path]::GetTempPath()) "musicplayer-api-$stamp"
        Write-Host "Publishing API to $publishDir..."
        dotnet publish (Join-Path $repoRoot 'backend\MusicPlayer.Api\MusicPlayer.Api.csproj') -c Release -o $publishDir --nologo
        if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
        $SourcePath = $publishDir
    }
    $SourcePath = [IO.Path]::GetFullPath($SourcePath)
    if (-not (Test-Path (Join-Path $SourcePath 'MusicPlayer.Api.dll'))) { throw "MusicPlayer.Api.dll not found in $SourcePath" }

    Write-Host 'Stopping service...'
    Stop-Api

    $binBackup = $null
    $dbBackup = $null
    if ($Migrate) {
        $dest = Join-Path $backupRoot $stamp
        $binBackup = Join-Path $dest 'bin'
        New-Item -ItemType Directory -Force -Path $binBackup | Out-Null
        robocopy $InstallPath $binBackup /E /NFL /NDL /NJH /NJS /NP /XD data logs backups | Out-Null
        if (Test-Path $dataDir) {
            $dbBackup = Join-Path $dest 'data'
            Copy-Item $dataDir $dbBackup -Recurse
        }
        Write-Host "Backup created: $dest"
    }

    Write-Host 'Copying binaries...'
    Copy-Binaries $SourcePath

    Write-Host 'Starting service...'
    Invoke-Nssm start $ServiceName

    if ($Migrate) {
        Write-Host 'Waiting for API (migrations run at startup)...'
        if (-not (Wait-Healthy)) {
            Write-Warning 'API did not become healthy. Rolling back.'
            Stop-Api
            Copy-Binaries $binBackup
            if ($dbBackup) {
                Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue
                Copy-Item $dbBackup $dataDir -Recurse
            }
            Invoke-Nssm start $ServiceName
            throw "Deployment failed and was rolled back. Check $(Join-Path $InstallPath 'logs\stderr.log')."
        }
        if (Test-Path $backupRoot) {
            Get-ChildItem $backupRoot -Directory | Sort-Object Name -Descending | Select-Object -Skip $KeepBackups |
                Remove-Item -Recurse -Force
        }
    } elseif (-not (Wait-Healthy)) {
        Write-Warning "Service started but health check failed at $HealthUrl. Check logs."
        exit 1
    }
    Write-Host 'Deployment succeeded.' -ForegroundColor Green
}
finally {
    if ($publishDir -and (Test-Path $publishDir)) { Remove-Item $publishDir -Recurse -Force }
}
