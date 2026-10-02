import { Component, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';

import { Api, ComercioDetalle, PagoPlan } from '../../core/api';

@Component({
  selector: 'app-super-admin-detalle',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './super-admin-detalle.html',
  styleUrls: ['./super-admin-detalle.css']
})
export class SuperAdminDetalle {
  planes = ['Gratuito', 'Basico', 'Premium'];

  comercioId: number;
  detalle = signal<ComercioDetalle | null>(null);
  // Historial de pagos del plan (Mercado Pago y transferencias marcadas como recibidas).
  pagos = signal<PagoPlan[]>([]);
  cargando = signal(false);
  error = signal<string | null>(null);

  actualizandoEstado = signal(false);
  actualizandoPlan = signal(false);
  renovando = signal(false);
  eliminando = signal(false);
  errorEliminar = signal<string | null>(null);

  constructor(private api: Api, private route: ActivatedRoute, private router: Router) {
    this.comercioId = Number(this.route.snapshot.paramMap.get('id'));
    this.cargarDetalle();
  }

  private cargarDetalle(): void {
    this.api.getPagosComercio(this.comercioId).subscribe({ next: p => this.pagos.set(p), error: () => this.pagos.set([]) });
    this.error.set(null);
    this.cargando.set(true);
    this.api.getComercioDetalle(this.comercioId).subscribe({
      next: d => {
        this.detalle.set(d);
        this.cargando.set(false);
      },
      error: () => {
        this.cargando.set(false);
        this.error.set('No pudimos cargar este comercio. Puede que no exista o que haya un problema de conexión.');
      }
    });
  }

  volver(): void {
    this.router.navigateByUrl('/super-admin');
  }

  toggleEstado(): void {
    const d = this.detalle();
    if (!d) return;
    this.actualizandoEstado.set(true);
    this.api.actualizarEstadoComercio(d.id, !d.activo).subscribe({
      next: () => {
        this.actualizandoEstado.set(false);
        this.cargarDetalle();
      },
      error: () => this.actualizandoEstado.set(false)
    });
  }

  cambiarPlan(nuevoPlan: string): void {
    const d = this.detalle();
    if (!d || nuevoPlan === d.planActual) return;
    this.actualizandoPlan.set(true);
    this.api.actualizarPlanComercio(d.id, nuevoPlan).subscribe({
      next: () => {
        this.actualizandoPlan.set(false);
        this.cargarDetalle();
      },
      error: () => this.actualizandoPlan.set(false)
    });
  }

  renovar(): void {
    const d = this.detalle();
    if (!d) return;
    this.renovando.set(true);
    this.api.renovarComercio(d.id).subscribe({
      next: () => {
        this.renovando.set(false);
        this.cargarDetalle();
      },
      error: () => this.renovando.set(false)
    });
  }

  // Eliminar vive acá (ya no en la fila de la tabla) y pide doble confirmación: primero un
  // aviso y después escribir el nombre del comercio. Igual que antes, solo se puede si está
  // pausado.
  eliminar(): void {
    const d = this.detalle();
    if (!d || d.activo) return;
    this.errorEliminar.set(null);

    if (!confirm(`¿Eliminar definitivamente "${d.nombre}"? Se borran también sus turnos, servicios, horarios, profesionales y sucursales. Esto no se puede deshacer.`)) return;
    const escrito = prompt(`Para confirmar, escribí el nombre del comercio: ${d.nombre}`);
    if (escrito === null) return;
    if (escrito.trim() !== d.nombre.trim()) {
      this.errorEliminar.set('El nombre no coincide. No se eliminó nada.');
      return;
    }

    this.eliminando.set(true);
    this.api.eliminarComercio(d.id).subscribe({
      next: () => {
        this.eliminando.set(false);
        this.router.navigateByUrl('/super-admin');
      },
      error: err => {
        this.eliminando.set(false);
        this.errorEliminar.set(err.error?.mensaje ?? 'No pudimos eliminar el comercio.');
      }
    });
  }

  // ================= GRÁFICO DE TURNOS POR SEMANA =================
  alturaBarraSemana(cantidad: number): number {
    const max = Math.max(1, ...(this.detalle()?.turnosPorSemana.map(s => s.cantidad) ?? [1]));
    return Math.max(4, Math.round((cantidad / max) * 100));
  }

  etiquetaSemana(desde: string, hasta: string): string {
    const d1 = new Date(desde);
    const d2 = new Date(hasta);
    const fmt = (d: Date) => `${d.getDate()}/${d.getMonth() + 1}`;
    return `${fmt(d1)}-${fmt(d2)}`;
  }

  diasParaVencimiento(): number | null {
    const fecha = this.detalle()?.fechaProximoPago;
    if (!fecha) return null;
    const hoy = new Date();
    hoy.setHours(0, 0, 0, 0);
    const vencimiento = new Date(fecha);
    vencimiento.setHours(0, 0, 0, 0);
    const msPorDia = 1000 * 60 * 60 * 24;
    return Math.round((vencimiento.getTime() - hoy.getTime()) / msPorDia);
  }

  etiquetaVencimiento(): string {
    const dias = this.diasParaVencimiento();
    if (dias === null) return 'Sin definir';
    if (dias > 0) return `${dias} día${dias === 1 ? '' : 's'}`;
    if (dias === 0) return 'Hoy';
    return `Venció hace ${-dias} día${dias === -1 ? '' : 's'}`;
  }
}
