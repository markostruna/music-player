import {
  ApplicationConfig,
  provideBrowserGlobalErrorListeners,
  provideZonelessChangeDetection,
} from '@angular/core';
import { provideRouter, withHashLocation } from '@angular/router';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { routes } from '../app/app.routes';
import { apiErrorInterceptor } from '@shared/interceptors/api-error.interceptor';
import { demoInterceptor } from './demo.interceptor';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideZonelessChangeDetection(),
    provideHttpClient(withInterceptors([apiErrorInterceptor, demoInterceptor])),
    provideRouter(routes, withHashLocation()),
  ],
};
