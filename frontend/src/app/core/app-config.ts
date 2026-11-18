import { InjectionToken } from '@angular/core';

export interface AppConfig {
  apiUrl: string;
  /** PrimeUI Community/Commercial license key. Never committed: see README. */
  primeLicense?: string;
}

export const APP_CONFIG = new InjectionToken<AppConfig>('APP_CONFIG');

/**
 * Read at startup so one build runs anywhere:
 * - config.json: committed defaults, rewritten by the deploy workflow (API address, license key)
 * - config.local.json: optional, git-ignored overrides for your machine (for example the license key)
 */
export async function loadConfig(): Promise<AppConfig> {
  const res = await fetch('config.json', { cache: 'no-store' });
  if (!res.ok) throw new Error('config.json is missing');
  const base = (await res.json()) as AppConfig;

  let local: Partial<AppConfig> = {};
  try {
    const r = await fetch('config.local.json', { cache: 'no-store' });
    if (r.ok && r.headers.get('content-type')?.includes('json')) local = await r.json();
  } catch { /* optional */ }

  const cfg = { ...base, ...local };
  return { apiUrl: cfg.apiUrl.replace(/\/+$/, ''), primeLicense: cfg.primeLicense || undefined };
}
