<#
.SYNOPSIS
    Populate dotnet user-secrets for the example web host from the repo-root .env file.

.DESCRIPTION
    The Swarmwright .NET app does NOT read .env — it reads appsettings.json + environment variables
    + user-secrets. This one-off helper bridges the gap: it reads .env and writes the LLM settings
    (and, when present, the Entra ID auth settings) into `dotnet user-secrets` for
    tests/Swarmwright.Example.WebHost, so the host starts without an "AzureOpenAI configuration
    section is missing" error and the admin SPA can authenticate.

    LLM provider (matching Program.cs):
      -Provider azure  (default)  -> AzureOpenAI:Endpoint / ApiKey / DeploymentName  (from MAF_AIF_*)
      -Provider vllm              -> OpenAI:Endpoint / Model / ApiKey                 (from VLLM_*)

    Entra ID auth (only when AZURE_AD_TENANT_ID is set in .env; provision with
    ./scripts/provision-app-registration.ps1):
      AzureAd:Instance / TenantId / ClientId / Audience / ClientSecret         (REST API token validation)
      SpaConfiguration:ClientId / TenantId / DefaultScope / RequiredPermissions (served at /api/spa-config)

    Secrets are stored per-user (Development environment only) and are never committed.

.EXAMPLE
    pwsh ./scripts/set-user-secrets.ps1
    Uses the MAF_AIF_* values from .env for the Azure OpenAI path, plus AZURE_AD_* auth if present.

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

# --- Entra ID (Azure AD) auth -------------------------------------------------------------------
# Only push when an app registration has been provisioned (AZURE_AD_TENANT_ID present). Without it
# the host runs unauthenticated (anonymous endpoints, no /api/spa-config), which is fine for a
# quick local spin-up but means the admin SPA login fails with "tenant not found".
if ($envMap.ContainsKey('AZURE_AD_TENANT_ID') -and $envMap['AZURE_AD_TENANT_ID']) {
    Write-Host "Setting Entra ID auth secrets (AzureAd + SpaConfiguration)" -ForegroundColor Cyan

    $instance = if ($envMap.ContainsKey('AZURE_AD_INSTANCE') -and $envMap['AZURE_AD_INSTANCE']) {
        $envMap['AZURE_AD_INSTANCE']
    }
    else {
        'https://login.microsoftonline.com/'
    }

    # REST API token validation (Microsoft.Identity.Web reads the AzureAd section).
    Set-Secret 'AzureAd:Instance'     $instance
    Set-Secret 'AzureAd:TenantId'     $envMap['AZURE_AD_TENANT_ID']
    Set-Secret 'AzureAd:ClientId'     $envMap['AZURE_AD_CLIENT_ID']
    Set-Secret 'AzureAd:Audience'     $envMap['AZURE_AD_AUDIENCE']
    Set-Secret 'AzureAd:ClientSecret' $envMap['AZURE_AD_CLIENT_SECRET']

    # Served anonymously from /api/spa-config so the React admin can configure MSAL. The combined
    # app registration acts as both the SPA client and the API resource, so the SPA ClientId is the
    # same AZURE_AD_CLIENT_ID.
    Set-Secret 'SpaConfiguration:ClientId'     $envMap['AZURE_AD_CLIENT_ID']
    Set-Secret 'SpaConfiguration:TenantId'     $envMap['AZURE_AD_TENANT_ID']
    Set-Secret 'SpaConfiguration:DefaultScope' $envMap['AZURE_AD_DEFAULT_SCOPE']

    # RequiredPermissions is a JSON array in config; user-secrets sets each element by index.
    # Clear a generous range first so a shorter scope list on re-run doesn't leave stale entries.
    for ($i = 0; $i -lt 10; $i++) {
        Remove-Secret "SpaConfiguration:RequiredPermissions:$i"
    }
    $scopes = @()
    if ($envMap.ContainsKey('AZURE_AD_API_SCOPES') -and $envMap['AZURE_AD_API_SCOPES']) {
        $scopes = $envMap['AZURE_AD_API_SCOPES'] -split '\s+' | Where-Object { $_ }
    }
    for ($i = 0; $i -lt $scopes.Count; $i++) {
        Set-Secret "SpaConfiguration:RequiredPermissions:$i" $scopes[$i]
    }
}
else {
    Write-Host "Skipping Entra ID auth (AZURE_AD_TENANT_ID not set in .env)." -ForegroundColor DarkYellow
    Write-Host "  Run ./scripts/provision-app-registration.ps1 first to enable SPA login." -ForegroundColor DarkYellow
}

Write-Host ""
Write-Host "Done. Run the host with:" -ForegroundColor Cyan
Write-Host "  dotnet run --project $Project"
Write-Host "Then browse https://localhost:7001"
