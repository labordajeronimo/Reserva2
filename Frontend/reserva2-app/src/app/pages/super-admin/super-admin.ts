import { Component, computed, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';

import { Api, ComercioAdmin, Dashboard, SuperAdminSession } from '../../core/api';
import { Session } from '../../core/session';
import { linkWhatsApp } from '../../core/whatsapp';

@Component({
  selector: 'app-super-admin',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  templateUrl: './super-admin.html',
  styleUrls: ['./super-admin.css']
})
export class SuperAdmin {
  planes = ['Gratuito', 'Basico', 'Premium'];
  ciclos = ['Mensual', 'Anual'];

  sesion: SuperAdminSession;

  dashboard = signal<Dashboard | null>(null);
  meses = ['Ene', 'Feb', 'Mar', 'Abr', 'May', 'Jun', 'Jul', 'Ago', 'Sep', 'Oct', 'Nov', 'Dic'];
  comercios = signal<ComercioAdmin[]>([]);
  cargandoComercios = signal(false);
  errorComercios = signal<string | null>(null);
  actualizandoEstadoId = signal<number | null>(null);
  actualizandoMontoId = signal<number | null>(null);
  renovandoId = signal<number | null>(null);

  // --- Búsqueda y filtros ---
  busqueda = signal('');
  filtroPlan = signal('Todos');
  filtroEstado = signal('Todos');

  comerciosFiltrados = computed(() => {
    const termino = this.busqueda().trim().toLowerCase();
    const plan = this.filtroPlan();
    const estado = this.filtroEstado();

    return this.comercios().filter(c => {
      const coincideTexto = !termino
        || c.nombre.toLowerCase().includes(termino)
        || c.aliasUrl.toLowerCase().includes(termino);
      const coincidePlan = plan === 'Todos' || c.planActual === plan;
      const coincideEstado = estado === 'Todos' || (estado === 'Activo' ? c.activo : !c.activo);
      return coincideTexto && coincidePlan && coincideEstado;
    });
  });

  constructor(private api: Api, private session: Session, private router: Router) {
    // El guard de la ruta ya garantiza que hay sesión antes de llegar acá.
    this.sesion = this.session.obtenerSuperAdmin()!;
    this.cargarTodo();
  }

  cerrarSesion(): void {
    this.session.cerrarSesionSuperAdmin();
    this.router.navigateByUrl('/super-admin/login');
  }

  linkAyudaWhatsApp(): string {
    return linkWhatsApp('Hola! Tengo una duda administrando Reserva2.');
  }

  private cargarTodo(): void {
    this.api.getDashboard().subscribe(d => this.dashboard.set(d));

    this.errorComercios.set(null);
    this.cargandoComercios.set(true);
    this.api.getComerciosAdmin().subscribe({
      next: c => {
        this.comercios.set(c);
        this.cargandoComercios.set(false);
      },
      error: () => {
        this.cargandoComercios.set(false);
        this.errorComercios.set('No pudimos cargar los comercios. Probá recargar la página o volver a ingresar.');
      }
    });
  }

  toggleEstado(c: ComercioAdmin): void {
    this.actualizandoEstadoId.set(c.id);
    this.api.actualizarEstadoComercio(c.id, !c.activo).subscribe({
      next: actualizado => {
        this.comercios.update(lista => lista.map(x => x.id === actualizado.id ? actualizado : x));
        this.actualizandoEstadoId.set(null);
        this.api.getDashboard().subscribe(d => this.dashboard.set(d));
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

  private diasParaVencimiento(c: ComercioAdmin): number | null {
    if (!c.fechaProximoPago) return null;
    const hoy = new Date();
    hoy.setHours(0, 0, 0, 0);
    const vencimiento = new Date(c.fechaProximoPago);
    vencimiento.setHours(0, 0, 0, 0);
    const msPorDia = 1000 * 60 * 60 * 24;
    return Math.round((vencimiento.getTime() - hoy.getTime()) / msPorDia);
  }

  etiquetaVencimiento(c: ComercioAdmin): string {
    const dias = this.diasParaVencimiento(c);
    if (dias === null) return 'Sin definir';
    if (dias > 0) return `${dias} día${dias === 1 ? '' : 's'}`;
    if (dias === 0) return 'Hoy';
    return `Venció hace ${-dias} día${dias === -1 ? '' : 's'}`;
  }

  vencimientoUrgente(c: ComercioAdmin): boolean {
    const dias = this.diasParaVencimiento(c);
    return dias !== null && dias <= 7;
  }

  renovar(c: ComercioAdmin): void {
    this.renovandoId.set(c.id);
    this.api.renovarComercio(c.id).subscribe({
      next: actualizado => {
        this.comercios.update(lista => lista.map(x => x.id === actualizado.id ? actualizado : x));
        this.renovandoId.set(null);
        this.api.getDashboard().subscribe(d => this.dashboard.set(d));
      },
      error: () => this.renovandoId.set(null)
    });
  }

  // ================= GRÁFICOS (CSS/SVG simple, sin librería) =================
  etiquetaMes(m: { anio: number; mes: number }): string {
    return this.meses[m.mes - 1];
  }

  // Altura de cada barra del gráfico de altas, como % del máximo del período (mínimo 4%
  // para que un mes en 0 siga mostrando el trazo de la barra, no quede invisible).
  alturaBarraAltas(cantidad: number): number {
    const max = Math.max(1, ...(this.dashboard()?.altasPorMes.map(m => m.cantidad) ?? [1]));
    return Math.max(4, Math.round((cantidad / max) * 100));
  }

  anchoBarraPlan(cantidad: number): number {
    const total = this.dashboard()?.totalComercios ?? 0;
    if (total === 0) return 0;
    return Math.round((cantidad / total) * 100);
  }
}
