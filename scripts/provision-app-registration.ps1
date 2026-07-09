<#
.SYNOPSIS
    Provision (idempotently) the Entra ID app registration that backs Swarmwright auth, mint a
    long-lived client secret, and write all the values into the repo-root .env file.

.DESCRIPTION
    Swarmwright's REST API and Admin SPA authenticate against a single-tenant Entra ID app
    registration. This script find-or-creates that registration in whatever tenant the current
    `az` session points at, then ensures it has:

      - Application ID URI  api://<clientId>
      - Delegated scopes    Swarm.Read, Swarm.Write          (consumed by the SPA / signed-in user)
      - App role            Swarm.Admin                       (consumed by machine-to-machine callers)
      - SPA redirect URIs   (PKCE; the React admin's login round-trip)
      - v2 access tokens    (Microsoft.Identity.Web expects accessTokenAcceptedVersion = 2)
      - Self pre-authorization for its own scopes (no consent prompt for local dev)

    It then resets the client secret to a single secret valid for -SecretMonths (default 24) and
    writes a managed block of AZURE_AD_* values into .env. The block is delimited by a marker
    comment and rewritten in place on every run, so re-running rotates the secret cleanly.

    Existing scope / role GUIDs are preserved across runs so previously issued tokens and the
    self pre-authorization keep working.

    Requires: the Azure CLI (`az`), logged in (`az login`) against the target tenant with rights to
    create app registrations (Application Administrator / Cloud Application Administrator, or owner).

.EXAMPLE
    pwsh ./scripts/provision-app-registration.ps1
    Provisions "Swarmwright" in the current az tenant with a 24-month secret and updates .env.

.EXAMPLE
    pwsh ./scripts/provision-app-registration.ps1 -DisplayName "Swarmwright Dev" -SecretMonths 6
    Uses a distinct registration name and a shorter-lived secret.

.NOTES
    After running this, run ./scripts/set-user-secrets.ps1 to push the values into dotnet
    user-secrets for tests/Swarmwright.Example.WebHost.
#>
[CmdletBinding()]
param(
    [string]$DisplayName = 'Swarmwright',

    [string]$EnvFile = (Join-Path $PSScriptRoot '..\.env'),

    [string[]]$RedirectUris = @(
        'http://localhost:5173/',
        'https://localhost:7001/',
        'https://localhost:7001/swarm-admin/'
    ),

    [int]$SecretMonths = 24,

    [string]$SecretDisplayName = 'swarmwright-local'
)

$ErrorActionPreference = 'Stop'

function Invoke-Az {
    # Run the az CLI and fail loudly on a non-zero exit (az writes errors to stderr but PowerShell
    # does not treat that as terminating on its own).
    param([Parameter(Mandatory)][string[]]$Arguments)
    $output = & az @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "az $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
    return $output
}

# --- Preconditions -----------------------------------------------------------------------------
if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw "Azure CLI ('az') not found on PATH. Install it and run 'az login' first."
}

$account = Invoke-Az @('account', 'show', '-o', 'json') | ConvertFrom-Json
$tenantId = $account.tenantId
Write-Host "Tenant:       $tenantId" -ForegroundColor Cyan
Write-Host "Subscription: $($account.name) ($($account.id))" -ForegroundColor Cyan
Write-Host "Signed in as: $($account.user.name)" -ForegroundColor Cyan

# --- Find or create the app registration -------------------------------------------------------
$existing = Invoke-Az @('ad', 'app', 'list', '--display-name', $DisplayName, '-o', 'json') | ConvertFrom-Json
if ($existing.Count -gt 1) {
    throw "Found $($existing.Count) app registrations named '$DisplayName'. Resolve the ambiguity (rename or delete extras) and re-run."
}

if ($existing.Count -eq 1) {
    $appId = $existing[0].appId
    Write-Host "Reusing existing app registration '$DisplayName' ($appId)." -ForegroundColor Green
}
else {
    Write-Host "Creating app registration '$DisplayName'..." -ForegroundColor Green
    $created = Invoke-Az @(
        'ad', 'app', 'create',
        '--display-name', $DisplayName,
        '--sign-in-audience', 'AzureADMyOrg',
        '-o', 'json') | ConvertFrom-Json
    $appId = $created.appId
}

$audience = "api://$appId"
Invoke-Az @('ad', 'app', 'update', '--id', $appId, '--identifier-uris', $audience) | Out-Null

# --- Preserve existing scope / role IDs across runs --------------------------------------------
$current = Invoke-Az @('ad', 'app', 'show', '--id', $appId, '-o', 'json') | ConvertFrom-Json

function Get-OrNewId {
    param($Collection, [string]$ValueField, [string]$Value)
    $match = $Collection | Where-Object { $_.$ValueField -eq $Value } | Select-Object -First 1
    if ($match) { return $match.id }
    return [guid]::NewGuid().ToString()
}

$readId = Get-OrNewId $current.api.oauth2PermissionScopes 'value' 'Swarm.Read'
$writeId = Get-OrNewId $current.api.oauth2PermissionScopes 'value' 'Swarm.Write'
$adminId = Get-OrNewId $current.appRoles 'value' 'Swarm.Admin'

# --- PATCH 1: scopes, app role, SPA redirect URIs, v2 tokens -----------------------------------
$patch1 = @{
    spa = @{ redirectUris = $RedirectUris }
    api = @{
        requestedAccessTokenVersion = 2
        oauth2PermissionScopes      = @(
            @{
                id                      = $readId
                adminConsentDisplayName = 'Read swarm state'
                adminConsentDescription = 'Allows the app to read swarm status, tasks, and artifacts on behalf of the signed-in user.'
                userConsentDisplayName  = 'Read your swarms'
                userConsentDescription  = 'Allows the app to read your swarm status, tasks, and artifacts.'
                value                   = 'Swarm.Read'
                type                    = 'User'
                isEnabled               = $true
            },
            @{
                id                      = $writeId
                adminConsentDisplayName = 'Create and manage swarms'
                adminConsentDescription = 'Allows the app to create, cancel, and recover swarms on behalf of the signed-in user.'
                userConsentDisplayName  = 'Create and manage your swarms'
                userConsentDescription  = 'Allows the app to create, cancel, and recover your swarms.'
                value                   = 'Swarm.Write'
                type                    = 'User'
                isEnabled               = $true
            }
        )
    }
    appRoles = @(
        @{
            id                 = $adminId
            allowedMemberTypes = @('Application')
            displayName        = 'Swarm Admin'
            description        = 'Full machine-to-machine access to the swarm REST API (read + write).'
            value              = 'Swarm.Admin'
            isEnabled          = $true
        }
    )
}

# --- PATCH 2: pre-authorize the app for its own scopes (SPA == resource; skip the consent prompt)
$patch2 = @{
    api = @{
        preAuthorizedApplications = @(
            @{ appId = $appId; delegatedPermissionIds = @($readId, $writeId) }
        )
    }
}

$graphUri = "https://graph.microsoft.com/v1.0/applications(appId='$appId')"
foreach ($patch in @($patch1, $patch2)) {
    $tmp = New-TemporaryFile
    try {
        # Depth 6 covers the nested scope/role/preauth arrays.
        $patch | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $tmp -Encoding utf8
        Invoke-Az @('rest', '--method', 'PATCH', '--uri', $graphUri, '--headers', 'Content-Type=application/json', '--body', "@$tmp") | Out-Null
    }
    finally {
        Remove-Item -LiteralPath $tmp -ErrorAction SilentlyContinue
    }
}
Write-Host "Configured scopes (Swarm.Read, Swarm.Write), role (Swarm.Admin), SPA redirects, v2 tokens." -ForegroundColor Green

# A service principal is required for sign-in / consent. Create it if missing.
& az ad sp create --id $appId 2>$null | Out-Null

# --- Mint a fresh client secret ----------------------------------------------------------------
$endDate = (Get-Date).AddMonths($SecretMonths).ToString('yyyy-MM-ddTHH:mm:ssZ')
Write-Host "Resetting client secret (valid until $endDate)..." -ForegroundColor Green
$secret = Invoke-Az @(
    'ad', 'app', 'credential', 'reset',
    '--id', $appId,
    '--display-name', $SecretDisplayName,
    '--end-date', $endDate,
    '--query', 'password',
    '-o', 'tsv')

# --- Write the managed block into .env ---------------------------------------------------------
$marker = '# --- Swarmwright app registration (managed by scripts/provision-app-registration.ps1) ---'
$block = @"
$marker
# Entra ID (Azure AD) app reg backing the Swarm REST API + Admin SPA auth.
# Real secret lives here only (./.env is git-ignored); scripts/set-user-secrets.ps1
# pushes these into ``dotnet user-secrets`` for the example host.
AZURE_AD_INSTANCE=https://login.microsoftonline.com/
AZURE_AD_TENANT_ID=$tenantId
AZURE_AD_CLIENT_ID=$appId
AZURE_AD_CLIENT_SECRET=$secret
AZURE_AD_AUDIENCE=$audience
AZURE_AD_DEFAULT_SCOPE=$audience/.default
# Space-separated delegated scopes the SPA requests (RequiredPermissions).
AZURE_AD_API_SCOPES=$audience/Swarm.Read $audience/Swarm.Write
"@

if (Test-Path -LiteralPath $EnvFile) {
    $existingText = Get-Content -LiteralPath $EnvFile -Raw
}
else {
    $existingText = ''
}
# Strip any previously written managed block (marker to end of file), then re-append.
$markerIndex = $existingText.IndexOf($marker)
if ($markerIndex -ge 0) {
    $existingText = $existingText.Substring(0, $markerIndex).TrimEnd()
}
$newText = ($existingText.TrimEnd() + "`r`n`r`n" + $block).TrimStart() + "`r`n"
Set-Content -LiteralPath $EnvFile -Value $newText -NoNewline -Encoding utf8

Write-Host ""
Write-Host "Done. App registration provisioned and .env updated:" -ForegroundColor Cyan
Write-Host "  Tenant:    $tenantId"
Write-Host "  ClientId:  $appId"
Write-Host "  Audience:  $audience"
Write-Host "  Secret:    valid until $endDate (written to $EnvFile)"
Write-Host ""
Write-Host "Next: pwsh ./scripts/set-user-secrets.ps1   # push these into dotnet user-secrets" -ForegroundColor Cyan
