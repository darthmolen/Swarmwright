# Setting Up Entra ID (Azure AD) for Swarms

Swarmwright's REST API validates Entra ID (Azure AD) bearer tokens and the admin SPA signs users in
with MSAL. Both are backed by an Entra ID **app registration** that publishes the scopes and roles
the host's authorization policies check for. This guide gets you from nothing to a working,
authenticated local host, then covers the production hardening.

By the end of the quickstart you will have:

1. A single **Swarmwright** app registration that both signs users in (SPA public client) and is the
   API resource that validates tokens.
2. A 24-month client secret and an `.env` file populated with the tenant/client/secret/scopes.
3. The example host serving real MSAL config from `GET /api/spa-config` and enforcing
   `Swarm.Read` / `Swarm.Write` on the swarm endpoints.

This covers the **single-tenant** case (`SupportedAccountTypes = AzureADMyOrg`). Production splits
and multi-tenant deltas are called out near the bottom.

## Quickstart — scripted (recommended)

From the repo root, with the Azure CLI installed and logged in against the target tenant
(`az login`), with rights to register applications (Application Administrator / Cloud Application
Administrator, or owner):

```powershell
pwsh ./scripts/provision-app-registration.ps1
pwsh ./scripts/set-user-secrets.ps1
dotnet run --project tests/Swarmwright.Example.WebHost   # https://localhost:7001
```

- [`provision-app-registration.ps1`](../scripts/provision-app-registration.ps1) is idempotent. It
  find-or-creates the `Swarmwright` app registration, exposes the `Swarm.Read` / `Swarm.Write`
  scopes and the `Swarm.Admin` app role, registers the SPA redirect URIs, sets v2 access tokens,
  pre-authorizes the app for its own scopes (no consent prompt for local dev), mints a 24-month
  client secret, and writes a managed `AZURE_AD_*` block into `.env`. Re-running rotates the secret
  cleanly and preserves the existing scope/role IDs.
- [`set-user-secrets.ps1`](../scripts/set-user-secrets.ps1) reads `.env` and pushes the `AzureAd:*`
  and `SpaConfiguration:*` values into `dotnet user-secrets` for the example host (Development only;
  never committed).

When no `AzureAd` configuration is present, the host runs **unauthenticated** — anonymous endpoints,
no `/api/spa-config` — which is fine for a quick spike but makes the SPA login fail with
"tenant not found".

### What the script provisions

| Facet | Value |
| --- | --- |
| Application ID URI | `api://<client-id>` |
| Delegated scopes | `Swarm.Read`, `Swarm.Write` (consumed by the SPA / signed-in user) |
| App role | `Swarm.Admin` (Applications only; machine-to-machine full access to the REST API) |
| SPA redirect URIs | `http://localhost:5173/` (Vite dev server), `https://localhost:7001/` and `https://localhost:7001/swarm-admin/` (example host) |
| Access token version | `2` (Microsoft.Identity.Web expects v2 tokens) |
| Pre-authorization | the app is pre-authorized for its own `Swarm.Read` / `Swarm.Write` scopes |
| Client secret | one secret, 24-month lifetime |

The single combined registration works because the SPA and the API resource are the same
application: the SPA requests `api://<client-id>/Swarm.Read`, and the pre-authorization means no
interactive consent is needed.

## Quickstart — manual (portal / az CLI)

If you can't run the script, create the same registration by hand.

1. **Register** — Entra admin center → **App registrations** → **+ New registration**. Name
   `Swarmwright`, single tenant, no redirect URI yet. Copy the **Application (client) ID**
   (`<CLIENT_ID>`).

   ```bash
   az ad app create --display-name "Swarmwright" --sign-in-audience AzureADMyOrg
   # copy appId -> <CLIENT_ID>
   ```

2. **Application ID URI** — Overview → **Add an Application ID URI** → accept `api://<CLIENT_ID>`.

   ```bash
   az ad app update --id <CLIENT_ID> --identifier-uris "api://<CLIENT_ID>"
   ```

3. **Expose scopes** — **Expose an API** → add `Swarm.Read` (Read swarm state) and `Swarm.Write`
   (Create and manage swarms), both enabled, admins-and-users consent.

4. **App role** — **App roles** → **+ Create app role** → `Swarm Admin`, value `Swarm.Admin`,
   allowed member type **Applications**.

5. **SPA platform** — **Authentication** → **+ Add a platform** → **Single-page application** →
   add the three redirect URIs from the table above (include the trailing slash). The SPA platform
   enables PKCE; do **not** add a Web platform for browser code.

6. **v2 tokens** — Manifest → set `requestedAccessTokenVersion` (a.k.a. `accessTokenAcceptedVersion`)
   to `2`.

7. **Client secret** — **Certificates & secrets** → **+ New client secret** → 24 months → copy the
   **Value** immediately.

   ```bash
   az ad app credential reset --id <CLIENT_ID> --years 2
   ```

8. Put the values into `.env` (see the `AZURE_AD_*` keys in [`.env.example`](../.env.example)), then
   run `pwsh ./scripts/set-user-secrets.ps1`.

## Host configuration

The host reads two sections. Real secrets belong in user-secrets or Key Vault, never in
`appsettings.json`. `set-user-secrets.ps1` writes both from `.env`.

```json
{
  "AzureAd": {
    "Instance": "https://login.microsoftonline.com/",
    "TenantId": "<your-tenant-id>",
    "ClientId": "<CLIENT_ID>",
    "Audience": "api://<CLIENT_ID>"
  },
  "SpaConfiguration": {
    "ClientId": "<CLIENT_ID>",
    "TenantId": "<your-tenant-id>",
    "DefaultScope": "api://<CLIENT_ID>/.default",
    "RequiredPermissions": [
      "api://<CLIENT_ID>/Swarm.Read",
      "api://<CLIENT_ID>/Swarm.Write"
    ]
  }
}
```

- **`AzureAd`** is read by `AddMicrosoftIdentityWebApi`. `Audience` must equal the Application ID
  URI. A `ClientSecret` is also written (for future confidential-client / on-behalf-of flows);
  plain token validation does not need it.
- **`SpaConfiguration`** is served anonymously from `/api/spa-config` and used to configure MSAL.js.
  Because the app registration is combined, the SPA `ClientId` is the same `<CLIENT_ID>`.

### How the host wires it up

The example host ([`Program.cs`](../tests/Swarmwright.Example.WebHost/Program.cs)) enables
authentication only when `AzureAd:ClientId` and `AzureAd:TenantId` are both present:

```csharp
var authEnabled = !string.IsNullOrWhiteSpace(builder.Configuration["AzureAd:ClientId"])
    && !string.IsNullOrWhiteSpace(builder.Configuration["AzureAd:TenantId"]);
if (authEnabled)
{
    builder.Services.AddSwarmAzureAdAuthentication(builder.Configuration); // JWT bearer via Microsoft.Identity.Web
    builder.Services.AddSwarmSpaConfiguration(builder.Configuration);      // binds SpaConfiguration
}

// ...after building the app:
if (authEnabled)
{
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapSwarmSpaConfig();          // anonymous GET /api/spa-config
}
app.MapSwarmEndpoints(useSwarmPolicies: authEnabled); // Swarm.Read / Swarm.Write when enabled
```

`AddSwarmAzureAdAuthentication` and `AddSwarmSpaConfiguration` live in
[`SwarmAuthenticationExtensions`](../src/Swarmwright.AspNetCore/Extensions/SwarmAuthenticationExtensions.cs);
`MapSwarmSpaConfig` in
[`SwarmAdminEndpointExtensions`](../src/Swarmwright.AspNetCore/Extensions/SwarmAdminEndpointExtensions.cs).
The `Swarm.Read` / `Swarm.Write` policies (scope **or** the `Swarm.Admin` app role) are registered
by `AddSwarmAuthorization`, defined in
[`SwarmAuthorizationExtensions`](../src/Swarmwright.AspNetCore/Extensions/SwarmAuthorizationExtensions.cs).

### MCP endpoint auth

The Swarm MCP server validates a separate claim surface. Enable Azure AD mode and (optionally)
override the role/scope names:

```json
{
  "SwarmMcp": {
    "AuthMode": "AzureAD",
    "EndpointPath": "/swarm/mcp"
  },
  "SwarmMcpAuthorization": {
    "ReadRole": "SwarmMcp.Read",
    "WriteRole": "SwarmMcp.Write",
    "ReadScope": "SwarmMcp.Read",
    "WriteScope": "SwarmMcp.Write"
  }
}
```

To let callers reach the MCP surface, add `SwarmMcp.Read` / `SwarmMcp.Write` as delegated scopes
(user-driven MCP) and/or app roles (app-to-app) on the registration. See
[mcp-server.md](mcp-server.md) for the auth modes and tool catalog.

## Verify

### SPA flow (delegated)

1. `dotnet run --project tests/Swarmwright.Example.WebHost`.
2. Confirm `GET /api/spa-config` returns your real `clientId` / `tenantId` / scopes:

   ```bash
   curl -s http://localhost:7000/api/spa-config
   ```

3. Confirm the swarm API is protected — without a token it returns **401**:

   ```bash
   curl -s -o /dev/null -w "%{http_code}\n" http://localhost:7000/api/swarm/templates   # 401
   ```

4. Browse `https://localhost:7001`, sign in, and decode the resulting access token at
   [jwt.ms](https://jwt.ms). Confirm `aud` = `api://<CLIENT_ID>`, `scp` contains
   `Swarm.Read Swarm.Write`, and `tid` = your tenant ID.

### App-to-app flow (client credentials)

For a machine caller granted the `Swarm.Admin` role (or the `SwarmMcp.*` roles):

```bash
curl -X POST "https://login.microsoftonline.com/<your-tenant-id>/oauth2/v2.0/token" \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "client_id=<CLIENT_ID>" \
  -d "scope=api://<CLIENT_ID>/.default" \
  -d "client_secret=<CLIENT_SECRET>" \
  -d "grant_type=client_credentials"
# Decode access_token at jwt.ms: roles should contain the app role(s) you granted.
```

## Production — split into separate registrations

The single combined registration is ideal for local development. For production you may prefer the
classic split, where the SPA cannot be impersonated by anything holding the API's secret and each
consumer gets least-privilege access:

1. A **Swarm API** app registration — the resource. It publishes the delegated scopes
   (`Swarm.Read`, `Swarm.Write`, and optionally `SwarmMcp.Read` / `SwarmMcp.Write`) and the app
   roles (`Swarm.Admin`, `SwarmMcp.Read`, `SwarmMcp.Write`). It never logs in interactively and
   holds no secret unless it calls downstream APIs.
2. A **Swarm Admin SPA** app registration — a public client (PKCE, no secret). Grant it delegated
   permission to the Swarm API's scopes and **grant admin consent** so users aren't prompted.
3. An optional **app-to-app client** registration — a confidential client with a secret or
   certificate, granted the **application** permissions (app roles) it needs on the Swarm API.

With the split, `AzureAd:ClientId` / `Audience` point at the **API** app, while
`SpaConfiguration:ClientId` points at the **SPA** app; `DefaultScope` and `RequiredPermissions` keep
referencing the API app's `api://<API_APP_ID>` URIs. Everything else in the host is identical.

## Common pitfalls

| Symptom | Cause | Fix |
| --- | --- | --- |
| SPA login fails with "tenant not found" | `/api/spa-config` returns empty config | Provision the app registration and run `set-user-secrets.ps1`; confirm `AzureAd:TenantId`/`ClientId` are set. |
| 401 on `/api/swarm/*` with a valid login | Token `aud` mismatch | `AzureAd:Audience` must exactly equal the Application ID URI (`api://<CLIENT_ID>`). |
| 403 on `/api/swarm/*` with a valid login | Missing scope on the token | The SPA didn't get the scopes — grant/consent `Swarm.Read` / `Swarm.Write` (the provisioning script's self pre-authorization handles this for the combined app). |
| 401 with `invalid_token` | v1 vs v2 token mismatch | Confirm `requestedAccessTokenVersion: 2` on the registration manifest. |
| App-to-app token has empty `roles` | App role not consented | App roles always require admin consent; confirm the API permissions page shows "Granted". |
| SPA bounces back to login | Redirect URI registered as Web instead of SPA | Delete the Web entry; add the redirect under **Single-page application** (PKCE). |
| `IDX10501: Signature validation failed` | Tenant mismatch | `AzureAd:TenantId` must match the tenant where the app is registered. |

## Multi-tenant deltas

If you ship the host to multiple tenants (`SupportedAccountTypes = AzureADMultipleOrgs`):

- Register the app as **multitenant**; `accessTokenAcceptedVersion: 2` matters even more.
- Set `AzureAd:TenantId` to `organizations` (or `common`). Microsoft.Identity.Web then accepts
  tokens from any tenant that has consented.
- Admin consent must be granted **per tenant** — the customer admin visits
  `https://login.microsoftonline.com/<their-tenant>/adminconsent?client_id=<CLIENT_ID>`.

Multi-tenant is significantly more work to operate; start single-tenant unless you have a concrete
deployment requirement.

## Related

- [swarm.md](swarm.md) — host integration and the REST authorization policies.
- [mcp-server.md](mcp-server.md) — the MCP-side auth modes (None / ApiKey / AzureAD).
- [admin.md](admin.md) — the SPA-side MSAL flow and the `/api/spa-config` contract.
