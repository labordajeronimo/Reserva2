import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';

import { Session } from './session';

export const adminClienteGuard: CanActivateFn = () => {
  const session = inject(Session);
  const router = inject(Router);
  return session.estaLogueado() ? true : router.createUrlTree(['/panel/login']);
};

export const superAdminGuard: CanActivateFn = () => {
  const session = inject(Session);
  const router = inject(Router);
  return session.obtenerSuperAdmin() ? true : router.createUrlTree(['/super-admin/login']);
};
