import { InjectionToken } from '@angular/core';

export interface AppConfig {
  apiUrl: string;
}

export const APP_CONFIG = new InjectionToken<AppConfig>('APP_CONFIG');

/**
 * Read at startup from config.json so one build runs anywhere:
 * the deploy workflow writes the real API address into that file.
 */
export async function loadConfig(): Promise<AppConfig> {
  const res = await fetch('config.json', { cache: 'no-store' });
  if (!res.ok) throw new Error('config.json is missing');
  const cfg = (await res.json()) as AppConfig;
  return { apiUrl: cfg.apiUrl.replace(/\/+$/, '') };
}
