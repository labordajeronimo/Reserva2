import { Component, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';

import { Api, ComercioAdmin, Metricas, SuperAdminSession } from '../../core/api';
import { Session } from '../../core/session';

@Component({
  selector: 'app-super-admin',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './super-admin.html',
  styleUrls: ['./super-admin.css']
})
export class SuperAdmin {
  planes = ['Gratuito', 'Basico', 'Premium'];
  ciclos = ['Mensual', 'Anual'];

  sesion: SuperAdminSession;

  metricas = signal<Metricas | null>(null);
  comercios = signal<ComercioAdmin[]>([]);
  actualizandoEstadoId = signal<number | null>(null);
  actualizandoMontoId = signal<number | null>(null);

  constructor(private api: Api, private session: Session, private router: Router) {
    // El guard de la ruta ya garantiza que hay sesión antes de llegar acá.
    this.sesion = this.session.obtenerSuperAdmin()!;
    this.cargarTodo();
  }

  cerrarSesion(): void {
    this.session.cerrarSesionSuperAdmin();
    this.router.navigateByUrl('/super-admin/login');
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

  guardarMontoAcordado(c: ComercioAdmin): void {
    this.actualizandoMontoId.set(c.id);
    this.api.actualizarMontoAcordado(c.id, c.montoMensualAcordado).subscribe({
      next: actualizado => {
        this.comercios.update(lista => lista.map(x => x.id === actualizado.id ? actualizado : x));
        this.actualizandoMontoId.set(null);
      },
      error: () => this.actualizandoMontoId.set(null)
    });
  }

  cambiarCicloFacturacion(c: ComercioAdmin, nuevoCiclo: string): void {
    if (nuevoCiclo === c.cicloFacturacion) return;
    this.api.actualizarCicloFacturacion(c.id, nuevoCiclo).subscribe(actualizado => {
      this.comercios.update(lista => lista.map(x => x.id === actualizado.id ? actualizado : x));
    });
  }
}
