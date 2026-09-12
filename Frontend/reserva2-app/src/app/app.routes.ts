import { Routes } from '@angular/router';

import { adminClienteGuard, superAdminGuard } from './core/auth.guard';

export const routes: Routes = [
  {
    path: '',
    loadComponent: () => import('./pages/landing/landing').then(m => m.Landing)
  },
  {
    path: 'terminos',
    loadComponent: () => import('./pages/legal/terminos').then(m => m.Terminos)
  },
  {
    path: 'privacidad',
    loadComponent: () => import('./pages/legal/privacidad').then(m => m.Privacidad)
  },
  {
    path: 'panel/login',
    loadComponent: () => import('./pages/panel-login/panel-login').then(m => m.PanelLogin)
  },
  {
    path: 'panel/olvide-password',
    loadComponent: () => import('./pages/panel-olvide-password/panel-olvide-password').then(m => m.PanelOlvidePassword)
  },
  {
    path: 'panel/reset-password',
    loadComponent: () => import('./pages/panel-reset-password/panel-reset-password').then(m => m.PanelResetPassword)
  },
  {
    path: 'cancelar-turno',
    loadComponent: () => import('./pages/cancelar-turno/cancelar-turno').then(m => m.CancelarTurno)
  },
  {
    path: 'panel',
    canActivate: [adminClienteGuard],
    loadComponent: () => import('./pages/panel/panel').then(m => m.Panel)
  },
  {
    path: 'super-admin/login',
    loadComponent: () => import('./pages/super-admin-login/super-admin-login').then(m => m.SuperAdminLogin)
  },
  {
    path: 'super-admin',
    canActivate: [superAdminGuard],
    loadComponent: () => import('./pages/super-admin/super-admin').then(m => m.SuperAdmin)
  },
  {
    // Página pública de cada comercio: reservados2.com/peluqueria-bella
    path: ':alias',
    loadComponent: () => import('./pages/reserva-publica/reserva-publica').then(m => m.ReservaPublica)
  },
  {
    // Cualquier ruta de más de un segmento que no matcheó nada arriba (ej. /foo/bar).
    path: '**',
    loadComponent: () => import('./pages/not-found/not-found').then(m => m.NotFound)
  }
];
