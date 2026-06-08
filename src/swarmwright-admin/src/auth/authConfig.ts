import { LogLevel, type Configuration } from '@azure/msal-browser';

export interface SpaConfig {
  clientId: string;
  tenantId: string;
  defaultScope: string;
  requiredPermissions: string[];
}

let spaConfig: SpaConfig | null = null;

export async function fetchSpaConfig(): Promise<SpaConfig> {
  if (spaConfig) {
    return spaConfig;
  }

  try {
    const response = await fetch('/api/spa-config');
    if (!response.ok) {
      throw new Error('Failed to fetch SPA configuration');
    }
    spaConfig = (await response.json()) as SpaConfig;
    return spaConfig;
  } catch (error) {
    console.error('Failed to fetch SPA configuration:', error);
    // Fallback to environment variables if API fails
    return {
      clientId: import.meta.env.VITE_AZURE_AD_CLIENT_ID ?? '',
      tenantId: import.meta.env.VITE_AZURE_AD_TENANT_ID ?? '',
      defaultScope: 'api://da821a1f-b704-4601-aedb-3f4e84dcff9a/.default',
      requiredPermissions: [
        'api://da821a1f-b704-4601-aedb-3f4e84dcff9a/Mcp.Sql.Read',
        'api://da821a1f-b704-4601-aedb-3f4e84dcff9a/Mcp.Sql.Write',
        'api://da821a1f-b704-4601-aedb-3f4e84dcff9a/Mcp.Sql.Update',
      ],
    };
  }
}

export function createMsalConfig(config: SpaConfig): Configuration {
  return {
    auth: {
      clientId: config.clientId,
      authority: `https://login.microsoftonline.com/${config.tenantId}`,
      // Redirect to the SPA's served base path (/swarm-admin/ bundled, / under the Vite dev server),
      // not the bare origin which the bundled host does not serve (404s, breaking the MSAL handshake).
      redirectUri: window.location.origin + import.meta.env.BASE_URL,
      postLogoutRedirectUri: window.location.origin + import.meta.env.BASE_URL,
      navigateToLoginRequestUrl: false,
    },
    cache: {
      cacheLocation: 'sessionStorage',
      storeAuthStateInCookie: false,
    },
    system: {
      loggerOptions: {
        // Suppress MSAL Info/Verbose chatter by default — it drowns out app
        // logs. Flip VITE_MSAL_VERBOSE=true in .env.local when you need
        // auth-protocol detail. Errors + warnings always surface.
        logLevel: import.meta.env.VITE_MSAL_VERBOSE === 'true'
          ? LogLevel.Verbose
          : LogLevel.Warning,
        loggerCallback: (level, message, containsPii) => {
          if (containsPii) return;
          switch (level) {
            case LogLevel.Error:
              console.error(`[msal] ${message}`);
              return;
            case LogLevel.Warning:
              console.warn(`[msal] ${message}`);
              return;
            case LogLevel.Info:
              if (import.meta.env.VITE_MSAL_VERBOSE === 'true') {
                console.info(`[msal] ${message}`);
              }
              return;
            case LogLevel.Verbose:
              if (import.meta.env.VITE_MSAL_VERBOSE === 'true') {
                console.debug(`[msal] ${message}`);
              }
              return;
          }
        },
      },
    },
  };
}

export function getLoginRequest(config: SpaConfig) {
  return {
    scopes: config.requiredPermissions?.length
      ? config.requiredPermissions
      : [config.defaultScope],
  };
}
