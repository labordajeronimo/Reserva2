import { Injectable } from '@angular/core';
import { LoginResponse } from './api';

@Injectable({ providedIn: 'root' })
export class Session {
  private readonly STORAGE_KEY = 'reserva2_session';

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

  estaLogueado(): boolean {
    return this.obtenerUsuario() !== null;
  }
}