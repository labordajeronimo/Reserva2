import { Component, computed, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';

import { Api, ComercioAdmin, ComercioEstadistica, Dashboard, SuperAdminResumen, SuperAdminSession } from '../../core/api';
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

  // Fecha de alta, si está pendiente de activación y turnos del mes, por id de comercio.
  // Si esta llamada falla, la tabla funciona igual (sin esos datos para ordenar/filtrar).
  estadisticas = signal<Map<number, ComercioEstadistica>>(new Map());
  // MRR, turnos/facturación del mes vs. el anterior, turnos por día, altas/bajas y rubros.
  // Si falla, esos bloques no se muestran.
  resumen = signal<SuperAdminResumen | null>(null);

  // Mismo valor que TopeTurnosMensualesGratuito en el backend; la alerta salta al 75%.
  readonly topeTurnosGratuito = 60;
  readonly avisoTurnosGratuito = 45;

  // --- Búsqueda, filtros y orden ---
  busqueda = signal('');
  filtroPlan = signal('Todos');
  filtroEstado = signal<'Todos' | 'Activo' | 'Pausado' | 'Pendiente'>('Todos');
  orden = signal<'recientes' | 'turnos' | 'vencimiento' | 'monto'>('recientes');

  comerciosFiltrados = computed(() => {
    const termino = this.busqueda().trim().toLowerCase();
    const plan = this.filtroPlan();
    const estado = this.filtroEstado();

    const filtrados = this.comercios().filter(c => {
      // "Dueño": el comercio no guarda un nombre de dueño aparte, así que se busca por el
      // email de la cuenta.
      const coincideTexto = !termino
        || c.nombre.toLowerCase().includes(termino)
        || c.aliasUrl.toLowerCase().includes(termino)
        || c.email.toLowerCase().includes(termino);
      const coincidePlan = plan === 'Todos' || c.planActual === plan;
      return coincideTexto && coincidePlan && (estado === 'Todos' || this.estadoComercio(c) === estado);
    });

    return [...filtrados].sort((a, b) => this.compararPorOrden(a, b));
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

  fechaHoyTexto(): string {
    const texto = new Date().toLocaleDateString('es-AR', { weekday: 'long', day: 'numeric', month: 'long' });
    return texto.charAt(0).toUpperCase() + texto.slice(1);
  }

  private cargarTodo(): void {
    this.api.getDashboard().subscribe(d => this.dashboard.set(d));
    this.api.getEstadisticasComercios().subscribe({
      next: lista => this.estadisticas.set(new Map(lista.map(e => [e.id, e]))),
      error: () => this.estadisticas.set(new Map())
    });
    this.cargarResumen();

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
        this.cargarResumen();
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
        this.cargarResumen();
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

  private cargarResumen(): void {
    this.api.getResumenSuperAdmin().subscribe({
      next: r => this.resumen.set(r),
      error: () => this.resumen.set(null)
    });
  }

  // ================= MÉTRICAS DEL NEGOCIO (Etapa B) =================
  // "Pagando" = activos con un plan pago (Básico o Premium).
  pagando(): number {
    return this.comercios().filter(c => c.activo && c.planActual !== 'Gratuito').length;
  }

  activos(): number {
    return this.comercios().filter(c => c.activo).length;
  }

  porcentaje(parte: number, total: number): number {
    return total > 0 ? Math.round((parte / total) * 100) : 0;
  }

  variacion(actual: number, anterior: number | null): number | null {
    if (anterior === null || anterior <= 0) return null;
    return Math.round(((actual - anterior) / anterior) * 100);
  }

  textoVariacion(actual: number, anterior: number | null): string {
    const v = this.variacion(actual, anterior) ?? 0;
    return `${v > 0 ? '+' : ''}${v}% vs. mes anterior`;
  }

  ticketPromedio(r: SuperAdminResumen): number | null {
    const pagando = this.pagando();
    return pagando > 0 ? Math.round(r.mrrActual / pagando) : null;
  }

  // ================= ALERTAS =================
  private diasDesdeUltimoAcceso(c: ComercioAdmin): number | null {
    const ultimo = this.estadisticas().get(c.id)?.ultimoAcceso;
    if (!ultimo) return null;
    const iso = /Z$|[+-]\d{2}:\d{2}$/.test(ultimo) ? ultimo : `${ultimo}Z`;
    return Math.floor((Date.now() - new Date(iso).getTime()) / (1000 * 60 * 60 * 24));
  }

  textoUltimoAcceso(c: ComercioAdmin): string {
    const dias = this.diasDesdeUltimoAcceso(c);
    if (dias === null) return 'Sin datos';
    if (dias === 0) return 'Hoy';
    return `Hace ${dias} día${dias === 1 ? '' : 's'}`;
  }

  alertas = computed(() => {
    const activosPagos = this.comercios().filter(c => c.activo && c.planActual !== 'Gratuito');
    return {
      vencidos: activosPagos.filter(c => (this.diasParaVencimiento(c) ?? 0) < 0),
      vencen7: activosPagos.filter(c => {
        const dias = this.diasParaVencimiento(c);
        return dias !== null && dias >= 0 && dias <= 7;
      }),
      pendientes: this.comercios().filter(c => this.estadoComercio(c) === 'Pendiente'),
      limiteGratis: this.comercios().filter(c => c.planActual === 'Gratuito' && this.turnosDelMes(c) >= this.avisoTurnosGratuito),
      // Sin dato de último acceso no se marca: el campo existe desde este cambio, así que
      // al principio casi nadie lo tiene y no corresponde alarmar por eso.
      sinActividad: this.comercios().filter(c => c.activo && (this.diasDesdeUltimoAcceso(c) ?? 0) > 14)
    };
  });

  hayAlertas(): boolean {
    const a = this.alertas();
    return a.vencidos.length + a.vencen7.length + a.pendientes.length + a.limiteGratis.length + a.sinActividad.length > 0;
  }

  // ================= SALUD =================
  // Caída de turnos: el mes en curso contra lo esperable según el mes anterior y cuántos
  // días del mes pasaron (si no, el día 2 todos parecerían en caída). Solo con un mes
  // anterior de al menos 4 turnos, para no sacar conclusiones de muy pocos datos.
  private caidaTurnos(c: ComercioAdmin): number {
    const e = this.estadisticas().get(c.id);
    if (!e || e.turnosMesAnterior < 4) return 0;
    const hoy = new Date();
    const diasDelMes = new Date(hoy.getFullYear(), hoy.getMonth() + 1, 0).getDate();
    const esperado = e.turnosMesAnterior * (hoy.getDate() / diasDelMes);
    if (esperado <= 0) return 0;
    return Math.max(0, 1 - e.turnosDelMes / esperado);
  }

  // Solo para comercios activos: En riesgo si venció, no entra hace más de 21 días o sus
  // turnos cayeron a la mitad; Atención si vence en 7 días o menos, no entra hace más de 7,
  // cayeron un 25% o está cerca del tope del plan gratuito.
  salud(c: ComercioAdmin): 'Sano' | 'Atención' | 'En riesgo' | null {
    if (!c.activo) return null;
    const venc = this.claseVencimiento(c);
    const sinAcceso = this.diasDesdeUltimoAcceso(c) ?? 0;
    const caida = this.caidaTurnos(c);
    if (venc === 'vencido' || sinAcceso > 21 || caida >= 0.5) return 'En riesgo';
    if (venc === 'alerta' || sinAcceso > 7 || caida >= 0.25
      || (c.planActual === 'Gratuito' && this.turnosDelMes(c) >= this.avisoTurnosGratuito)) return 'Atención';
    return 'Sano';
  }

  claseSalud(c: ComercioAdmin): string {
    const s = this.salud(c);
    return s === 'En riesgo' ? 'riesgo' : s === 'Atención' ? 'atencion' : s === 'Sano' ? 'sano' : 'sin';
  }

  // ================= GRÁFICOS DEL RESUMEN =================
  alturaRelativa(valor: number, valores: number[]): number {
    const max = Math.max(1, ...valores);
    return valor === 0 ? 2 : Math.max(4, Math.round((valor / max) * 100));
  }

  maxAltasBajas(r: SuperAdminResumen): number[] {
    return r.altasYBajasPorMes.flatMap(m => [m.altas, m.bajas]);
  }

  cantidadesTurnosPorDia(r: SuperAdminResumen): number[] {
    return r.turnosPorDia.map(d => d.cantidad);
  }

  etiquetaFechaCorta(fecha: string): string {
    const [, m, d] = fecha.split('-');
    return `${d}/${m}`;
  }

  anchoRubro(r: SuperAdminResumen, cantidad: number): number {
    const max = Math.max(1, ...r.porRubro.map(x => x.cantidad));
    return Math.round((cantidad / max) * 100);
  }

  // ================= ESTADO, ORDEN Y PRESENTACIÓN =================
  // "Pendiente" = nunca se activó (mismo criterio que el KPI del dashboard); "Pausado" = se
  // activó alguna vez y ahora está inactivo.
  estadoComercio(c: ComercioAdmin): 'Activo' | 'Pausado' | 'Pendiente' {
    if (c.activo) return 'Activo';
    return this.estadisticas().get(c.id)?.pendiente ? 'Pendiente' : 'Pausado';
  }

  // Los KPI de Pausados y Pendientes filtran la tabla; un segundo clic saca el filtro.
  filtrarPorKpi(estado: 'Pausado' | 'Pendiente'): void {
    this.filtroEstado.set(this.filtroEstado() === estado ? 'Todos' : estado);
  }

  turnosDelMes(c: ComercioAdmin): number {
    return this.estadisticas().get(c.id)?.turnosDelMes ?? 0;
  }

  private compararPorOrden(a: ComercioAdmin, b: ComercioAdmin): number {
    switch (this.orden()) {
      case 'turnos':
        return this.turnosDelMes(b) - this.turnosDelMes(a);
      case 'monto':
        return (b.montoMensualAcordado ?? 0) - (a.montoMensualAcordado ?? 0);
      case 'vencimiento': {
        // Sin fecha de vencimiento (ej. gratuitos) van al final.
        const da = this.diasParaVencimiento(a) ?? Number.MAX_SAFE_INTEGER;
        const db = this.diasParaVencimiento(b) ?? Number.MAX_SAFE_INTEGER;
        return da - db;
      }
      default: {
        const fa = this.estadisticas().get(a.id)?.fechaAlta ?? '';
        const fb = this.estadisticas().get(b.id)?.fechaAlta ?? '';
        return fb.localeCompare(fa) || b.id - a.id;
      }
    }
  }

  iniciales(nombre: string): string {
    const palabras = nombre.trim().split(/\s+/).filter(Boolean);
    return palabras.slice(0, 2).map(p => p[0].toUpperCase()).join('') || '?';
  }

  // Pastilla de vencimiento: verde > 7 días, ámbar <= 7, roja si ya venció, "—" si es gratuito.
  claseVencimiento(c: ComercioAdmin): 'ok' | 'alerta' | 'vencido' | 'sin' {
    if (c.planActual === 'Gratuito') return 'sin';
    const dias = this.diasParaVencimiento(c);
    if (dias === null) return 'sin';
    if (dias < 0) return 'vencido';
    return dias <= 7 ? 'alerta' : 'ok';
  }

  textoVencimiento(c: ComercioAdmin): string {
    if (c.planActual === 'Gratuito') return '—';
    return this.etiquetaVencimiento(c);
  }

  // MRR de cada plan con el mismo criterio que el ingreso mensual estimado del dashboard:
  // suma de montos acordados de comercios activos.
  mrrPorPlan(plan: string): number {
    return this.comercios().filter(c => c.activo && c.planActual === plan).reduce((t, c) => t + (c.montoMensualAcordado ?? 0), 0);
  }

  nombrePlan(plan: string): string {
    return plan === 'Basico' ? 'Básico' : plan;
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
