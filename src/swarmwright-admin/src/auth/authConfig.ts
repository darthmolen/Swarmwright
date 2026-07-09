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
    // Fallback to environment variables when the host's /api/spa-config is unavailable.
    // The API resource defaults to the SPA client id (Swarmwright's combined app registration);
    // override with VITE_AZURE_AD_API_CLIENT_ID if you split the SPA and API into separate apps.
    const clientId = import.meta.env.VITE_AZURE_AD_CLIENT_ID ?? '';
    const tenantId = import.meta.env.VITE_AZURE_AD_TENANT_ID ?? '';
    const apiClientId = import.meta.env.VITE_AZURE_AD_API_CLIENT_ID ?? clientId;
    const apiPrefix = apiClientId ? `api://${apiClientId}` : '';
    return {
      clientId,
      tenantId,
      defaultScope: apiPrefix ? `${apiPrefix}/.default` : '',
      requiredPermissions: apiPrefix
        ? [`${apiPrefix}/Swarm.Read`, `${apiPrefix}/Swarm.Write`]
        : [],
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
