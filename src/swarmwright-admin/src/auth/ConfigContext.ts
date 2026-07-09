import { createContext, useContext } from 'react';
import type { SpaConfig } from './authConfig';

export const ConfigContext = createContext<SpaConfig | null>(null);

export function useConfig(): SpaConfig {
  const context = useContext(ConfigContext);
  if (!context) {
    throw new Error('useConfig must be used within a ConfigProvider');
  }
  return context;
}
