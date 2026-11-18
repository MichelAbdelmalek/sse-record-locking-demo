import { provideHttpClient, withFetch } from '@angular/common/http';
import { ApplicationConfig, provideZoneChangeDetection } from '@angular/core';
import { provideAnimationsAsync } from '@angular/platform-browser/animations/async';
import { provideRouter } from '@angular/router';
import { routes } from './app.routes';
import { APP_CONFIG, AppConfig } from './core/app-config';

export function buildAppConfig(config: AppConfig): ApplicationConfig {
  return {
    providers: [
      provideZoneChangeDetection({ eventCoalescing: true }),
      provideRouter(routes),
      provideHttpClient(withFetch()),
      // PrimeNG 17 animates overlays, dialogs and toasts with @angular/animations.
      provideAnimationsAsync(),
      { provide: APP_CONFIG, useValue: config },
    ],
  };
}
