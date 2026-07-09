import { useCallback } from 'react';
import { useMsal } from '@azure/msal-react';
import { InteractionRequiredAuthError } from '@azure/msal-browser';
import { useConfig } from './ConfigContext';

export function useAuthToken() {
  const { instance, accounts } = useMsal();
  const spaConfig = useConfig();

  const getToken = useCallback(async (): Promise<string | null> => {
    if (accounts.length === 0) return null;

    const scopes = spaConfig.requiredPermissions?.length
      ? spaConfig.requiredPermissions
      : [spaConfig.defaultScope];

    try {
      const result = await instance.acquireTokenSilent({
        scopes,
        account: accounts[0],
      });
      return result.accessToken;
    } catch (error) {
      if (error instanceof InteractionRequiredAuthError) {
        try {
          const result = await instance.acquireTokenPopup({ scopes });
          return result.accessToken;
        } catch (popupError) {
          console.error('Token popup failed:', popupError);
          return null;
        }
      }
      console.error('Token acquisition failed:', error);
      return null;
    }
  }, [instance, accounts, spaConfig]);

  return { getToken };
}
