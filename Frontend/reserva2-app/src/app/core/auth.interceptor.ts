import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';

import { Session } from './session';

export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const session = inject(Session);
  const token = session.obtenerToken() ?? session.obtenerTokenSuperAdmin();
  if (!token) return next(req);

  return next(req.clone({ setHeaders: { Authorization: `Bearer ${token}` } }));
};
