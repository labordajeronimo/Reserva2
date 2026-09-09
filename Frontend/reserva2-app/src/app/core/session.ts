import { Injectable } from '@angular/core';
import { LoginResponse, SuperAdminSession } from './api';

@Injectable({ providedIn: 'root' })
export class Session {
  private readonly STORAGE_KEY = 'reserva2_session';
  private readonly SUPER_ADMIN_STORAGE_KEY = 'reserva2_super_admin_session';

  iniciarSesion(datos: LoginResponse): void {
    localStorage.setItem(this.STORAGE_KEY, JSON.stringify(datos));
  }

  cerrarSesion(): void {
    localStorage.removeItem(this.STORAGE_KEY);
  }

  obtenerUsuario(): LoginResponse | null {
    const data = localStorage.getItem(this.STORAGE_KEY);
    return data ? JSON.parse(data) : null;
  }

  obtenerToken(): string | null {
    return this.obtenerUsuario()?.token ?? null;
  }

  estaLogueado(): boolean {
    return this.obtenerUsuario() !== null;
  }

  // --- Super Admin (sesión separada, no se mezcla con la del Admin Cliente) ---
  iniciarSesionSuperAdmin(datos: SuperAdminSession): void {
    localStorage.setItem(this.SUPER_ADMIN_STORAGE_KEY, JSON.stringify(datos));
  }

  cerrarSesionSuperAdmin(): void {
    localStorage.removeItem(this.SUPER_ADMIN_STORAGE_KEY);
  }

  obtenerSuperAdmin(): SuperAdminSession | null {
    const data = localStorage.getItem(this.SUPER_ADMIN_STORAGE_KEY);
    return data ? JSON.parse(data) : null;
  }

  obtenerTokenSuperAdmin(): string | null {
    return this.obtenerSuperAdmin()?.token ?? null;
  }
}