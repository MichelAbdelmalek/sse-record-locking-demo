import { provideHttpClient, withFetch } from '@angular/common/http';
import { ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideRouter } from '@angular/router';
import Aura from '@primeuix/themes/aura';
import { providePrimeNG } from 'primeng/config';
import { routes } from './app.routes';
import { APP_CONFIG, AppConfig } from './core/app-config';

export function buildAppConfig(config: AppConfig): ApplicationConfig {
  return {
    providers: [
      provideBrowserGlobalErrorListeners(),
      provideRouter(routes),
      provideHttpClient(withFetch()),
      { provide: APP_CONFIG, useValue: config },
      providePrimeNG({
        license: config.primeLicense,
        theme: {
          preset: Aura,
          // Light only for now; Tailwind utilities win over PrimeNG through this layer order.
          options: { darkModeSelector: '.app-dark', cssLayer: { name: 'primeng', order: 'theme, base, primeng' } },
        },
      }),
    ],
  };
}
