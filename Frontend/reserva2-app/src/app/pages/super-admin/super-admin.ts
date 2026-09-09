import { Component, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';

import { Api, ComercioAdmin, Metricas, SuperAdminSession } from '../../core/api';
import { Session } from '../../core/session';

@Component({
  selector: 'app-super-admin',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  templateUrl: './super-admin.html',
  styleUrls: ['./super-admin.css']
})
export class SuperAdmin {
  planes = ['Gratuito', 'Basico', 'Premium'];

  sesion = signal<SuperAdminSession | null>(null);
  errorAuth = signal<string | null>(null);
  cargandoAuth = signal(false);

  loginEmail = '';
  loginPassword = '';

  metricas = signal<Metricas | null>(null);
  comercios = signal<ComercioAdmin[]>([]);
  actualizandoEstadoId = signal<number | null>(null);

  constructor(private api: Api, private session: Session) {
    this.sesion.set(this.session.obtenerSuperAdmin());
    if (this.sesion()) this.cargarTodo();
  }

  login(): void {
    this.errorAuth.set(null);
    this.cargandoAuth.set(true);
    this.api.superAdminLogin(this.loginEmail.trim(), this.loginPassword).subscribe({
      next: resp => {
        this.session.iniciarSesionSuperAdmin(resp);
        this.sesion.set(resp);
        this.cargandoAuth.set(false);
        this.cargarTodo();
      },
      error: () => {
        this.cargandoAuth.set(false);
        this.errorAuth.set('Email o contraseña incorrectos.');
      }
    });
  }

  cerrarSesion(): void {
    this.session.cerrarSesionSuperAdmin();
    this.sesion.set(null);
  }

  private cargarTodo(): void {
    this.api.getMetricas().subscribe(m => this.metricas.set(m));
    this.api.getComerciosAdmin().subscribe(c => this.comercios.set(c));
  }

  toggleEstado(c: ComercioAdmin): void {
    this.actualizandoEstadoId.set(c.id);
    this.api.actualizarEstadoComercio(c.id, !c.activo).subscribe({
      next: actualizado => {
        this.comercios.update(lista => lista.map(x => x.id === actualizado.id ? actualizado : x));
        this.actualizandoEstadoId.set(null);
        this.api.getMetricas().subscribe(m => this.metricas.set(m));
      },
      error: () => this.actualizandoEstadoId.set(null)
    });
  }

  cambiarPlan(c: ComercioAdmin, nuevoPlan: string): void {
    if (nuevoPlan === c.planActual) return;
    this.api.actualizarPlanComercio(c.id, nuevoPlan).subscribe(actualizado => {
      this.comercios.update(lista => lista.map(x => x.id === actualizado.id ? actualizado : x));
    });
  }
}
