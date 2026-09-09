import { Routes } from '@angular/router';

export const routes: Routes = [
  {
    path: '',
    loadComponent: () => import('./pages/landing/landing').then(m => m.Landing)
  },
  {
    path: 'panel',
    loadComponent: () => import('./pages/panel/panel').then(m => m.Panel)
  },
  {
    // Página pública de cada comercio: reserva2.app/peluqueria-bella
    path: ':alias',
    loadComponent: () => import('./pages/reserva-publica/reserva-publica').then(m => m.ReservaPublica)
  }
];