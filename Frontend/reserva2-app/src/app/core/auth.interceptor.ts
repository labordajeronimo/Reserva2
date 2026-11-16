import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, throwError } from 'rxjs';

import { API_BASE } from './api';
import { Session, tipoSesionDeLaRuta } from './session';

// La sesión viaja en una cookie HttpOnly (la pone y la lee solo el backend). A cada pedido a
// nuestra API se le agrega:
//  - withCredentials, para que el navegador mande la cookie (en desarrollo la API está en
//    otro puerto; en producción es el mismo dominio y no cambia nada);
//  - X-Reserva2-Sesion, que le dice al backend qué sesión usar (panel o Super Admin, que
//    pueden estar abiertas a la vez) y además frena CSRF: otro sitio no puede agregar
//    este header, y sin él el backend ignora la cookie.
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  if (!req.url.startsWith(API_BASE)) return next(req);

  const session = inject(Session);
  const tipo = tipoSesionDeLaRuta();
  const authReq = req.clone({ withCredentials: true, setHeaders: { 'X-Reserva2-Sesion': tipo } });

  return next(authReq).pipe(
    catchError((err: HttpErrorResponse) => {
      // Un 401 con una sesión local abierta = la cookie venció, se cerró en otro lado o se
      // cambió la contraseña. (El 401 del login con datos incorrectos pasa sin sesión local.)
      if (err.status === 401) {
        if (tipo === 'superadmin' && session.obtenerSuperAdmin()) {
          session.olvidarSesionSuperAdmin();
          window.location.href = '/super-admin/login';
        } else if (tipo === 'comercio' && session.estaLogueado() && window.location.pathname.startsWith('/panel')) {
          session.olvidarSesion();
          window.location.href = '/panel/login';
        }
      }
      return throwError(() => err);
    })
  );
};
