import { bootstrapApplication } from '@angular/platform-browser';

import { AppComponent } from './app/app.component';
import { appConfig } from './app/app.config';

// No NgModule anywhere in this workspace (G1 [R]1). bootstrapApplication + standalone components.
bootstrapApplication(AppComponent, appConfig).catch((error: unknown) => console.error(error));
