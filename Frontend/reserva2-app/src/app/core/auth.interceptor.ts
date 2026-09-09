import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, throwError } from 'rxjs';

import { Session } from './session';

export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const session = inject(Session);
  const token = session.obtenerToken() ?? session.obtenerTokenSuperAdmin();
  const authReq = token ? req.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : req;

  return next(authReq).pipe(
    catchError((err: HttpErrorResponse) => {
      // Un 401 en un request que SÍ llevaba token propio significa sesión vencida o inválida
      // (un 401 de /auth/login por credenciales incorrectas nunca lleva token, así que no cae acá).
      if (token && err.status === 401) {
        if (window.location.pathname.startsWith('/super-admin')) {
          session.cerrarSesionSuperAdmin();
          window.location.href = '/super-admin/login';
        } else {
          session.cerrarSesion();
          window.location.href = '/panel/login';
        }
      }
      return throwError(() => err);
    })
  );
};
