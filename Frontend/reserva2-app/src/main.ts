import { bootstrapApplication } from '@angular/platform-browser';
import { appConfig } from './app/app.config';
import { App } from './app/app';

// iOS Safari solo pinta :active al apoyar el dedo si la página escucha touchstart; sin esto
// los botones recién reaccionan al levantar el dedo. Es pasivo: no frena el scroll.
document.addEventListener('touchstart', () => {}, { passive: true });

bootstrapApplication(App, appConfig)
  .catch((err) => console.error(err));
