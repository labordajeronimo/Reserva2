import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, throwError } from 'rxjs';

import { Session } from './session';

export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const session = inject(Session);
  // Las dos sesiones (Admin Cliente y Super Admin) pueden coexistir en el mismo navegador
  // (localStorage no las pisa entre sí), así que hay que elegir el token según la ruta
  // actual y no por un orden fijo, o una request a /super-admin podía viajar con el
  // token de Admin Cliente y volver 403 sin que nadie lo note.
  const enRutaSuperAdmin = window.location.pathname.startsWith('/super-admin');
  const token = enRutaSuperAdmin
    ? session.obtenerTokenSuperAdmin() ?? session.obtenerToken()
    : session.obtenerToken() ?? session.obtenerTokenSuperAdmin();
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
