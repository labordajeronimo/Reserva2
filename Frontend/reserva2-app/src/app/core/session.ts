import { Injectable } from '@angular/core';
import { API_BASE, LoginResponse, SuperAdminSession } from './api';

// Tipo de sesión que usa cada pedido a la API (header X-Reserva2-Sesion): el Super Admin y el
// panel de un comercio pueden estar abiertos a la vez en el mismo navegador, cada uno con su
// cookie, y el backend elige cuál leer según este header.
export type TipoSesion = 'comercio' | 'superadmin';

export function tipoSesionDeLaRuta(): TipoSesion {
  return window.location.pathname.startsWith('/super-admin') ? 'superadmin' : 'comercio';
}

// El token de sesión vive en una cookie HttpOnly que pone el backend: ningún script de la
// página la puede leer (ni uno inyectado). Acá solo se guardan los datos para mostrar
// (nombre, plan, etc.), que no sirven para hacerse pasar por nadie.
@Injectable({ providedIn: 'root' })
export class Session {
  private readonly STORAGE_KEY = 'reserva2_session';
  private readonly SUPER_ADMIN_STORAGE_KEY = 'reserva2_super_admin_session';

  iniciarSesion(datos: LoginResponse): void {
    // Por si quedó guardada una sesión de antes, que todavía traía el token.
    const { token: _viejo, ...sinToken } = datos as LoginResponse & { token?: string };
    localStorage.setItem(this.STORAGE_KEY, JSON.stringify(sinToken));
  }

  cerrarSesion(): void {
    localStorage.removeItem(this.STORAGE_KEY);
    this.borrarCookie('comercio');
  }

  // Solo borra los datos locales (la cookie ya no sirve, ej. venció).
  olvidarSesion(): void {
    localStorage.removeItem(this.STORAGE_KEY);
  }

  obtenerUsuario(): LoginResponse | null {
    const data = localStorage.getItem(this.STORAGE_KEY);
    if (!data) return null;
    const usuario = JSON.parse(data) as LoginResponse & { token?: string };
    // Sesión guardada con el formato viejo (token en localStorage): no hay cookie, hay que volver a entrar.
    if (usuario.token !== undefined) {
      localStorage.removeItem(this.STORAGE_KEY);
      return null;
    }
    return usuario;
  }

  estaLogueado(): boolean {
    return this.obtenerUsuario() !== null;
  }

  actualizarPerfilEnSesion(nombre: string, telefonoNotificaciones: string, datosBancarios: string, logoUrl: string | null): void {
    const actual = this.obtenerUsuario();
    if (!actual) return;
    this.iniciarSesion({ ...actual, nombre, telefonoNotificaciones, datosBancarios, logoUrl });
  }

  // --- Super Admin (sesión separada, no se mezcla con la del Admin Cliente) ---
  iniciarSesionSuperAdmin(datos: SuperAdminSession): void {
    localStorage.setItem(this.SUPER_ADMIN_STORAGE_KEY, JSON.stringify(datos));
  }

  cerrarSesionSuperAdmin(): void {
    localStorage.removeItem(this.SUPER_ADMIN_STORAGE_KEY);
    this.borrarCookie('superadmin');
  }

  olvidarSesionSuperAdmin(): void {
    localStorage.removeItem(this.SUPER_ADMIN_STORAGE_KEY);
  }

  obtenerSuperAdmin(): SuperAdminSession | null {
    const data = localStorage.getItem(this.SUPER_ADMIN_STORAGE_KEY);
    if (!data) return null;
    const sesion = JSON.parse(data) as SuperAdminSession & { token?: string };
    if (sesion.token !== undefined) {
      localStorage.removeItem(this.SUPER_ADMIN_STORAGE_KEY);
      return null;
    }
    return sesion;
  }

  // La cookie es HttpOnly: solo el backend la puede borrar. Va con fetch directo (no con
  // HttpClient) para no depender del interceptor, y si falla no importa: igual vence sola.
  private borrarCookie(tipo: TipoSesion): void {
    fetch(`${API_BASE}/auth/logout`, {
      method: 'POST',
      credentials: 'include',
      headers: { 'X-Reserva2-Sesion': tipo }
    }).catch(() => {});
  }
}
