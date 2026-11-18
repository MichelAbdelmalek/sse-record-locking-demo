import { bootstrapApplication } from '@angular/platform-browser';
import { App } from './app/app';
import { buildAppConfig } from './app/app.config';
import { loadConfig } from './app/core/app-config';

loadConfig()
  .then((cfg) => bootstrapApplication(App, buildAppConfig(cfg)))
  .catch((err) => {
    console.error(err);
    document.body.textContent = 'The demo could not start: config.json is missing or invalid.';
  });
