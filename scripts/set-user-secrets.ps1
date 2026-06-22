<#
.SYNOPSIS
    Populate dotnet user-secrets for the example web host from the repo-root .env file.

.DESCRIPTION
    The Swarmwright .NET app does NOT read .env — it reads appsettings.json + environment variables
    + user-secrets. This one-off helper bridges the gap: it reads .env and writes the LLM settings
    into `dotnet user-secrets` for tests/Swarmwright.Example.WebHost, so the host starts without an
    "AzureOpenAI configuration section is missing" error.

    Two providers (matching Program.cs):
      -Provider azure  (default)  -> AzureOpenAI:Endpoint / ApiKey / DeploymentName  (from MAF_AIF_*)
      -Provider vllm              -> OpenAI:Endpoint / Model / ApiKey                 (from VLLM_*)

    Secrets are stored per-user (Development environment only) and are never committed.

.EXAMPLE
    pwsh ./scripts/set-user-secrets.ps1
    Uses the MAF_AIF_* values from .env for the Azure OpenAI path.

.EXAMPLE
    pwsh ./scripts/set-user-secrets.ps1 -Provider vllm
    Points the host at the local vLLM server (http://localhost:<VLLM_PORT>/v1).
#>
[CmdletBinding()]
param(
    [ValidateSet('azure', 'vllm')]
    [string]$Provider = 'azure',

    [string]$Project = (Join-Path $PSScriptRoot '..\tests\Swarmwright.Example.WebHost'),

    [string]$EnvFile = (Join-Path $PSScriptRoot '..\.env')
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $EnvFile)) {
    throw "No .env found at '$EnvFile'. Copy .env.example to .env first."
}

# Parse .env (KEY=VALUE; ignores blank lines and # comments; strips surrounding quotes).
$envMap = @{}
foreach ($raw in Get-Content -LiteralPath $EnvFile) {
    $line = $raw.Trim()
    if (-not $line -or $line.StartsWith('#') -or -not $line.Contains('=')) { continue }
    $idx = $line.IndexOf('=')
    $key = $line.Substring(0, $idx).Trim()
    $val = $line.Substring($idx + 1).Trim().Trim('"')
    $envMap[$key] = $val
}

# Ensure the project has a UserSecretsId (no-op if already present).
dotnet user-secrets init --project $Project | Out-Null

function Set-Secret([string]$Key, [string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) {
        Write-Host "  skip $Key (empty in .env)" -ForegroundColor DarkYellow
        return
    }
    dotnet user-secrets set $Key $Value --project $Project | Out-Null
    Write-Host "  set  $Key" -ForegroundColor Green
}

function Remove-Secret([string]$Key) {
    dotnet user-secrets remove $Key --project $Project 2>$null | Out-Null
}

Write-Host "Setting user-secrets for $Project (provider: $Provider)" -ForegroundColor Cyan

if ($Provider -eq 'azure') {
    # Clear the OpenAI-compatible endpoint so Program.cs takes the Azure branch.
    Remove-Secret 'OpenAI:Endpoint'
    Set-Secret 'AzureOpenAI:Endpoint'       $envMap['MAF_AIF_ENDPOINT']
    Set-Secret 'AzureOpenAI:ApiKey'         $envMap['MAF_AIF_API_KEY']
    Set-Secret 'AzureOpenAI:DeploymentName' $envMap['MAF_AIF_MODEL']
}
else {
    $vllmPort = if ($envMap.ContainsKey('VLLM_PORT') -and $envMap['VLLM_PORT']) { $envMap['VLLM_PORT'] } else { '8000' }
    Set-Secret 'OpenAI:Endpoint' "http://localhost:$vllmPort/v1"
    Set-Secret 'OpenAI:Model'    $envMap['VLLM_MODEL']
    Set-Secret 'OpenAI:ApiKey'   'swarmwright'
}

Write-Host ""
Write-Host "Done. Run the host with:" -ForegroundColor Cyan
Write-Host "  dotnet run --project $Project"
Write-Host "Then browse https://localhost:7001"
