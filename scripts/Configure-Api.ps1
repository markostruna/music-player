<#
.SYNOPSIS
    Registers the MusicPlayer API as an NSSM service and configures XAMPP Apache to proxy API calls to it.
.EXAMPLE
    .\Configure-Api.ps1 -InstallPath D:\Apps\MusicPlayerApi
.EXAMPLE
    .\Configure-Api.ps1 -InstallPath D:\Apps\MusicPlayerApi -Remove
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InstallPath,
    [string]$ServiceName = 'MusicPlayerApi',
    [string]$XamppPath = 'C:\xampp',
    [string]$NssmPath = 'nssm.exe',
    [string]$ApiUrl = 'http://127.0.0.1:5080',
    # Public path Apache exposes; the Angular dev proxy uses /music-api and strips it.
    [string]$UrlPrefix = '/music-api',
    [string]$ApacheServiceName = 'Apache2.4',
    [string]$BootstrapEmail,
    [string]$BootstrapPassword,
    # Removes the service and the Apache config (and optionally the installed files).
    [switch]$Remove,
    [switch]$DeleteFiles
)

$ErrorActionPreference = 'Stop'

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated (Administrator) PowerShell.'
}
if (-not (Get-Command $NssmPath -ErrorAction SilentlyContinue)) { throw "nssm not found ('$NssmPath'). Add it to PATH or pass -NssmPath." }

$InstallPath = [IO.Path]::GetFullPath($InstallPath)
$UrlPrefix = '/' + $UrlPrefix.Trim('/')
$httpdConf = Join-Path $XamppPath 'apache\conf\httpd.conf'
$apiConf = Join-Path $XamppPath "apache\conf\extra\httpd-$ServiceName.conf"
$beginMarker = "# BEGIN $ServiceName"
$endMarker = "# END $ServiceName"

function Invoke-Nssm { & $NssmPath @args; if ($LASTEXITCODE -ne 0) { throw "nssm $($args -join ' ') failed ($LASTEXITCODE)" } }

function Test-ServiceExists { [bool](Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) }

function Restart-Apache {
    if (Get-Service -Name $ApacheServiceName -ErrorAction SilentlyContinue) {
        Restart-Service -Name $ApacheServiceName -Force
        Write-Host "Apache service '$ApacheServiceName' restarted."
    } else {
        Write-Warning "Apache is not installed as a service ('$ApacheServiceName'). Restart Apache from the XAMPP control panel."
    }
}

function Test-ApacheConfig {
    # httpd writes "Syntax OK" to stderr, which would trip $ErrorActionPreference = 'Stop'.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $out = & (Join-Path $XamppPath 'apache\bin\httpd.exe') -t 2>&1 | Out-String
    $code = $LASTEXITCODE
    $ErrorActionPreference = $previous
    if ($code -ne 0) { throw "Apache config test failed:`n$out" }
}

function Remove-ApacheBlock {
    $text = [IO.File]::ReadAllText($httpdConf)
    $pattern = "(?s)\r?\n?$([regex]::Escape($beginMarker)).*?$([regex]::Escape($endMarker))\r?\n?"
    $new = [regex]::Replace($text, $pattern, "`r`n")
    if ($new -ne $text) { [IO.File]::WriteAllText($httpdConf, $new) }
}

if ($Remove) {
    if (Test-ServiceExists) {
        Write-Host "Removing service '$ServiceName'..."
        try { & $NssmPath stop $ServiceName 2>&1 | Out-Null } catch { }
        Invoke-Nssm remove $ServiceName confirm
    } else { Write-Host "Service '$ServiceName' not found, skipping." }

    if (Test-Path $httpdConf) {
        Copy-Item $httpdConf "$httpdConf.$(Get-Date -Format yyyyMMddHHmmss).bak"
        Remove-ApacheBlock
        Remove-Item $apiConf -ErrorAction SilentlyContinue
        Test-ApacheConfig
        Restart-Apache
    }
    if ($DeleteFiles -and (Test-Path $InstallPath)) {
        Remove-Item $InstallPath -Recurse -Force
        Write-Host "Deleted $InstallPath"
    }
    Write-Host 'Configuration removed.' -ForegroundColor Green
    return
}

if (-not (Test-Path $httpdConf)) { throw "Apache config not found: $httpdConf (use -XamppPath)." }
New-Item -ItemType Directory -Force -Path $InstallPath, (Join-Path $InstallPath 'data'), (Join-Path $InstallPath 'logs') | Out-Null

# --- NSSM service ---
$exe = Join-Path $InstallPath 'MusicPlayer.Api.exe'
if (Test-ServiceExists) {
    Write-Host "Service '$ServiceName' exists; updating."
    Invoke-Nssm set $ServiceName Application $exe
} else {
    Invoke-Nssm install $ServiceName $exe
}
$envVars = @(
    'ASPNETCORE_ENVIRONMENT=Production',
    "Api__Url=$ApiUrl",
    "MUSICPLAYER_DATABASE_PATH=$(Join-Path $InstallPath 'data\music-library.db')"
)
if ($BootstrapEmail) { $envVars += "MUSICPLAYER_BOOTSTRAP_EMAIL=$BootstrapEmail" }
if ($BootstrapPassword) { $envVars += "MUSICPLAYER_BOOTSTRAP_PASSWORD=$BootstrapPassword" }

Invoke-Nssm set $ServiceName AppDirectory $InstallPath
Invoke-Nssm set $ServiceName DisplayName 'Music Player API'
Invoke-Nssm set $ServiceName Start SERVICE_AUTO_START
Invoke-Nssm set $ServiceName AppEnvironmentExtra @envVars
Invoke-Nssm set $ServiceName AppStdout (Join-Path $InstallPath 'logs\stdout.log')
Invoke-Nssm set $ServiceName AppStderr (Join-Path $InstallPath 'logs\stderr.log')
Invoke-Nssm set $ServiceName AppRotateFiles 1
Invoke-Nssm set $ServiceName AppRotateBytes 10485760

# --- Apache reverse proxy ---
Copy-Item $httpdConf "$httpdConf.$(Get-Date -Format yyyyMMddHHmmss).bak"
$target = $ApiUrl.TrimEnd('/')
$conf = @"
# Managed by Configure-Api.ps1 - do not edit by hand.
ProxyPreserveHost On
ProxyRequests Off
RequestHeader set X-Forwarded-Proto expr=%{REQUEST_SCHEME}
ProxyPass $UrlPrefix/ $target/
ProxyPassReverse $UrlPrefix/ $target/
"@
Set-Content -Path $apiConf -Value $conf -Encoding ASCII

Remove-ApacheBlock
# Enable required modules if commented out; add them only if absent entirely.
$modules = 'proxy_module', 'proxy_http_module', 'headers_module'
$lines = Get-Content $httpdConf | ForEach-Object {
    if ($_ -match '^\s*#\s*(LoadModule\s+(proxy_module|proxy_http_module|headers_module)\b.*)$') { $Matches[1] } else { $_ }
}
Set-Content -Path $httpdConf -Value $lines -Encoding ASCII
$block = @($beginMarker)
foreach ($m in $modules) {
    if (-not ($lines -match "^\s*LoadModule\s+$m\b")) {
        $file = if ($m -eq 'headers_module') { 'mod_headers.so' } else { "mod_$($m -replace '_module$','').so" }
        $block += "LoadModule $m modules/$file"
    }
}
$block += "Include `"conf/extra/httpd-$ServiceName.conf`""
$block += $endMarker
Add-Content -Path $httpdConf -Value ("`r`n" + ($block -join "`r`n")) -Encoding ASCII

try { Test-ApacheConfig }
catch {
    Remove-ApacheBlock
    Remove-Item $apiConf -ErrorAction SilentlyContinue
    throw
}
Restart-Apache

Write-Host "Configured. Service '$ServiceName' -> $ApiUrl, Apache path $UrlPrefix/ proxies to it." -ForegroundColor Green
Write-Host "Now deploy binaries with: .\Deploy-Api.ps1 -InstallPath '$InstallPath'"
