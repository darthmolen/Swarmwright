import { useState, useEffect, type ReactNode } from 'react';
import { PublicClientApplication } from '@azure/msal-browser';
import { MsalProvider } from '@azure/msal-react';
import { fetchSpaConfig, createMsalConfig, type SpaConfig } from './authConfig';
import { ConfigContext } from './ConfigContext';

interface AuthProviderProps {
  children: ReactNode;
}

export function AuthProvider({ children }: AuthProviderProps) {
  const [msalInstance, setMsalInstance] = useState<PublicClientApplication | null>(null);
  const [spaConfig, setSpaConfig] = useState<SpaConfig | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;

    async function init() {
      try {
        const config = await fetchSpaConfig();
        if (cancelled) return;

        const msalConfig = createMsalConfig(config);
        const instance = new PublicClientApplication(msalConfig);
        await instance.initialize();

        if (cancelled) return;
        setSpaConfig(config);
        setMsalInstance(instance);
      } catch (err) {
        if (cancelled) return;
        console.error('Failed to initialize MSAL:', err);
        setError('Failed to load authentication configuration. Please refresh the page.');
      }
    }

    init();
    return () => { cancelled = true; };
  }, []);

  if (error) {
    return (
      <div style={{ padding: '20px', textAlign: 'center' }}>
        <h1>Configuration Error</h1>
        <p>{error}</p>
      </div>
    );
  }

  if (!msalInstance || !spaConfig) {
    return (
      <div style={{ padding: '20px', textAlign: 'center', color: '#94a3b8' }}>
        <p>Initializing authentication...</p>
      </div>
    );
  }

  return (
    <ConfigContext.Provider value={spaConfig}>
      <MsalProvider instance={msalInstance}>
        {children}
      </MsalProvider>
    </ConfigContext.Provider>
  );
}
