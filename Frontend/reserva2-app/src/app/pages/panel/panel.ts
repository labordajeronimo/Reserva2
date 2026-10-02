import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';

import { Api, Turno, Servicio, Horario, Profesional, Sucursal, LoginResponse, Historial, Ganancias, WhatsAppConfig, Resumen, MercadoPagoConfig, urlArchivo } from '../../core/api';
import { Session } from '../../core/session';
import { linkWhatsApp } from '../../core/whatsapp';
import { PlanSelector } from '../../shared/plan-selector/plan-selector';

type TabPanel = 'inicio' | 'turnos' | 'servicios' | 'horarios' | 'profesionales' | 'sucursales' | 'historial' | 'ganancias' | 'whatsapp' | 'plan' | 'perfil';

// Items del menú lateral, en el orden en que se dibujan. "soloPremium" replica la condición
// que antes tenían los botones de Ganancias y WhatsApp en la barra de pestañas.
const MENU_PANEL: { tab: TabPanel; etiqueta: string; soloPremium?: boolean }[] = [
  { tab: 'inicio', etiqueta: 'Inicio' },
  { tab: 'turnos', etiqueta: 'Turnos' },
  { tab: 'historial', etiqueta: 'Historial' },
  { tab: 'servicios', etiqueta: 'Servicios' },
  { tab: 'horarios', etiqueta: 'Horarios' },
  { tab: 'profesionales', etiqueta: 'Profesionales' },
  { tab: 'sucursales', etiqueta: 'Sucursales' },
  { tab: 'ganancias', etiqueta: 'Ganancias', soloPremium: true },
  { tab: 'whatsapp', etiqueta: 'WhatsApp', soloPremium: true },
  { tab: 'plan', etiqueta: 'Mi Plan' },
  { tab: 'perfil', etiqueta: 'Perfil' }
];

// Mismo valor que HorasLimiteParaConfirmar en el backend: una pre-reserva que no se confirma
// en ese plazo desde que se creó deja de ocupar el horario (y el endpoint de turnos deja de
// devolverla). Se usa solo para mostrar cuánto le falta a cada una en la pestaña Inicio.
const HORAS_LIMITE_PARA_CONFIRMAR = 2;

const DIAS = ['Domingo', 'Lunes', 'Martes', 'Miércoles', 'Jueves', 'Viernes', 'Sábado'];
const MESES = ['Enero', 'Febrero', 'Marzo', 'Abril', 'Mayo', 'Junio', 'Julio', 'Agosto', 'Septiembre', 'Octubre', 'Noviembre', 'Diciembre'];

// Precios de lista (los mismos que se muestran en la landing y en el selector de plan).
// El monto real que cobra cada comercio puede diferir si el Super Admin acordó un monto
// puntual (MontoMensualAcordado); esto es solo para precargar el mensaje de WhatsApp con
// una cifra de referencia calculada según cuántos profesionales tiene el comercio en total
// (sumando todas sus sucursales).
const PRECIOS_PLAN: Record<string, { unico: number; porProfesional: number }> = {
  Gratuito: { unico: 0, porProfesional: 0 },
  Basico: { unico: 7000, porProfesional: 4800 },
  Premium: { unico: 10000, porProfesional: 7500 }
};
const PRECIOS_PLAN_ANUAL: Record<string, { unico: number; porProfesional: number }> = {
  Gratuito: { unico: 0, porProfesional: 0 },
  Basico: { unico: 63000, porProfesional: 43200 },
  Premium: { unico: 90000, porProfesional: 67500 }
};

@Component({
  selector: 'app-panel',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink, PlanSelector],
  templateUrl: './panel.html',
  styleUrls: ['./panel.css']
})
export class Panel {
  dias = DIAS;
  meses = MESES;

  sesion: LoginResponse;
  tabActiva = signal<TabPanel>('inicio');

  // --- Perfil del comercio ---
  perfilNombre = '';
  perfilTelefono = '';
  perfilDatosBancarios = '';
  guardandoPerfil = signal(false);
  errorPerfil = signal<string | null>(null);
  perfilGuardadoOk = signal(false);
  subiendoLogo = signal(false);
  errorLogo = signal<string | null>(null);

  passwordActual = '';
  passwordNueva = '';
  passwordNuevaRepetida = '';
  cambiandoPassword = signal(false);
  errorPassword = signal<string | null>(null);
  passwordCambiadaOk = signal(false);

  // --- Mi Plan ---
  // El dueño ya no cambia el plan solo: elige el que quiere y lo pide por WhatsApp; el
  // cambio real lo hace el Super Admin una vez coordinado el pago.
  planSeleccionado = '';
  cicloSeleccionado = '';
  precioPlanPedido = signal<number | null>(null);
  private precioPlanRequestId = 0;

  // Pago del plan con Mercado Pago: monto que calcula el backend (respeta el monto acordado
  // con el Super Admin) para renovar el plan actual y para el plan elegido en "Cambiar de plan".
  pagoRenovacion = signal<{ monto: number; disponible: boolean } | null>(null);
  pagoPlanPedido = signal<{ monto: number; disponible: boolean } | null>(null);
  private pagoPlanPedidoRequestId = 0;
  iniciandoPagoPlan = signal(false);
  errorPagoPlan = signal<string | null>(null);
  avisoPagoPlan = signal<{ tipo: 'ok' | 'info' | 'error'; texto: string } | null>(null);

  // --- Mercado Pago del comercio (cobro de señas), en Perfil ---
  mercadoPago = signal<MercadoPagoConfig | null>(null);
  cargandoMercadoPago = signal(false);
  errorMercadoPago = signal<string | null>(null);
  avisoMercadoPago = signal<string | null>(null);
  private mercadoPagoCargadoAlMenosUnaVez = false;

  // --- Turnos ---
  turnos = signal<Turno[]>([]);
  linkCopiado = signal(false);

  // --- Calendario del día (una columna por profesional). Arma la grilla con los turnos ya
  // cargados — no pega al backend de nuevo, ya está todo en el signal "turnos". Es por día
  // (no por semana) porque con columnas por profesional, una semana entera no entra cómoda
  // en pantalla. ---
  readonly pxPorHora = 120;
  vistaTurnos = signal<'calendario' | 'lista'>('calendario');
  agendaDia = signal<Date>(this.soloFecha(new Date()));
  ahora = signal(new Date());

  // Se guarda el id (no el objeto) para que, al recargar los turnos después de confirmar o
  // cancelar, el detalle muestre el estado nuevo sin tener que volver a seleccionarlo.
  turnoSeleccionadoId = signal<number | null>(null);
  turnoSeleccionado = computed(() => this.turnos().find(t => t.id === this.turnoSeleccionadoId()) ?? null);

  private soloFecha(d: Date): Date {
    return new Date(d.getFullYear(), d.getMonth(), d.getDate());
  }

  mismoDia(a: Date, b: Date): boolean {
    return a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate();
  }

  esHoyAgenda(): boolean {
    return this.mismoDia(this.agendaDia(), this.ahora());
  }

  turnosDelDia(dia: Date): Turno[] {
    return this.turnos()
      .filter(t => this.mismoDia(new Date(t.fechaHoraInicio), dia))
      .sort((a, b) => a.fechaHoraInicio.localeCompare(b.fechaHoraInicio));
  }

  // Si el comercio no cargó profesionales (dueño único), se usa una sola columna "Vos" que
  // agrupa todos los turnos del día. Si hay profesionales pero algún turno del día no tiene
  // uno asignado (o es de un profesional ya borrado), se suma una columna "Sin asignar" para
  // que ese turno no quede invisible en el calendario.
  columnasAgenda(): { id: number | null; nombre: string }[] {
    const profesionales = this.profesionales();
    if (profesionales.length === 0) return [{ id: null, nombre: 'Vos' }];
    const columnas: { id: number | null; nombre: string }[] = profesionales.map(p => ({ id: p.id, nombre: p.nombre }));
    if (this.turnosDelDia(this.agendaDia()).some(t => this.esTurnoSinAsignar(t))) columnas.push({ id: null, nombre: 'Sin asignar' });
    return columnas;
  }

  private esTurnoSinAsignar(t: Turno): boolean {
    return t.profesionalId === null || !this.profesionales().some(p => p.id === t.profesionalId);
  }

  turnosColumnaAgenda(columnaId: number | null): Turno[] {
    const delDia = this.turnosDelDia(this.agendaDia());
    if (this.profesionales().length === 0) return delDia;
    return delDia.filter(t => columnaId === null ? this.esTurnoSinAsignar(t) : t.profesionalId === columnaId);
  }

  private minutosDelDia(fechaIso: string): number {
    const f = new Date(fechaIso);
    return f.getHours() * 60 + f.getMinutes();
  }

  // Minuto de fin dentro del día del turno (si termina pasada la medianoche, se corta en 24h).
  private minutoFin(t: Turno): number {
    const fin = this.mismoDia(new Date(t.fechaHoraFin), new Date(t.fechaHoraInicio)) ? this.minutosDelDia(t.fechaHoraFin) : 24 * 60;
    return Math.max(this.minutosDelDia(t.fechaHoraInicio) + 15, fin);
  }

  private horaAMinutos(hora: string): number {
    const [h, m] = hora.split(':').map(Number);
    return h * 60 + m;
  }

  // Rango visible: el horario general de ese día de la semana (o 9 a 20 si no hay), estirado
  // para que entren todos los turnos del día aunque caigan fuera del horario.
  rangoHorasAgenda(): { desde: number; hasta: number } {
    const dia = this.agendaDia();
    const horariosDelDia = this.horarios().filter(h => h.diaSemana === dia.getDay());
    let desde = horariosDelDia.length ? Math.min(...horariosDelDia.map(h => this.horaAMinutos(h.horaInicio))) : 9 * 60;
    let hasta = horariosDelDia.length ? Math.max(...horariosDelDia.map(h => this.horaAMinutos(h.horaFin))) : 20 * 60;
    for (const t of this.turnosDelDia(dia)) {
      desde = Math.min(desde, this.minutosDelDia(t.fechaHoraInicio));
      hasta = Math.max(hasta, this.minutoFin(t));
    }
    const horaDesde = Math.floor(desde / 60);
    return { desde: horaDesde, hasta: Math.min(24, Math.max(horaDesde + 1, Math.ceil(hasta / 60))) };
  }

  horasAgenda(): number[] {
    const { desde, hasta } = this.rangoHorasAgenda();
    return Array.from({ length: hasta - desde }, (_, i) => desde + i);
  }

  alturaAgendaPx(): number {
    const { desde, hasta } = this.rangoHorasAgenda();
    return (hasta - desde) * this.pxPorHora;
  }

  etiquetaHora(h: number): string {
    return `${String(h).padStart(2, '0')}:00`;
  }

  // Posición de cada bloque: arriba según la hora de inicio y alto según la duración. Si dos
  // turnos de la misma columna se pisan, se reparten el ancho en carriles lado a lado.
  bloquesColumnaAgenda(columnaId: number | null): { turno: Turno; top: number; alto: number; izquierda: number; ancho: number }[] {
    const inicioRango = this.rangoHorasAgenda().desde * 60;
    const bloques: { turno: Turno; ini: number; fin: number; carril: number; carriles: number }[] = [];
    let grupo: typeof bloques = [];
    let finesCarriles: number[] = [];
    let finGrupo = -1;

    const cerrarGrupo = () => {
      for (const b of grupo) b.carriles = finesCarriles.length;
      grupo = [];
      finesCarriles = [];
    };

    for (const t of this.turnosColumnaAgenda(columnaId)) {
      const ini = this.minutosDelDia(t.fechaHoraInicio);
      const fin = this.minutoFin(t);
      if (ini >= finGrupo) cerrarGrupo();
      let carril = finesCarriles.findIndex(f => f <= ini);
      if (carril === -1) {
        carril = finesCarriles.length;
        finesCarriles.push(fin);
      } else {
        finesCarriles[carril] = fin;
      }
      const b = { turno: t, ini, fin, carril, carriles: 1 };
      bloques.push(b);
      grupo.push(b);
      finGrupo = Math.max(finGrupo, fin);
    }
    cerrarGrupo();

    return bloques.map(b => ({
      turno: b.turno,
      top: (b.ini - inicioRango) / 60 * this.pxPorHora,
      alto: Math.max(24, (b.fin - b.ini) / 60 * this.pxPorHora - 2),
      izquierda: (b.carril / b.carriles) * 100,
      ancho: 100 / b.carriles
    }));
  }

  // Línea naranja "ahora": solo si el día elegido es hoy y la hora cae dentro del rango.
  ahoraTopPx(): number | null {
    if (!this.esHoyAgenda()) return null;
    const { desde, hasta } = this.rangoHorasAgenda();
    const minutos = this.ahora().getHours() * 60 + this.ahora().getMinutes();
    if (minutos < desde * 60 || minutos > hasta * 60) return null;
    return (minutos - desde * 60) / 60 * this.pxPorHora;
  }

  // Tira de 7 días (lunes a domingo) de la semana del día elegido, con la cantidad de turnos
  // no cancelados de cada día.
  semanaAgenda(): { fecha: Date; nombre: string; cantidad: number }[] {
    const base = this.agendaDia();
    const lunes = new Date(base.getFullYear(), base.getMonth(), base.getDate() - ((base.getDay() + 6) % 7));
    return Array.from({ length: 7 }, (_, i) => {
      const fecha = new Date(lunes.getFullYear(), lunes.getMonth(), lunes.getDate() + i);
      return {
        fecha,
        nombre: this.dias[fecha.getDay()].substring(0, 3),
        cantidad: this.turnosDelDia(fecha).filter(t => t.estadoReserva !== 3).length
      };
    });
  }

  irADia(d: Date): void {
    this.agendaDia.set(this.soloFecha(d));
    this.turnoSeleccionadoId.set(null);
  }

  agendaDiaAnterior(): void {
    const d = new Date(this.agendaDia());
    d.setDate(d.getDate() - 1);
    this.irADia(d);
  }

  agendaDiaSiguiente(): void {
    const d = new Date(this.agendaDia());
    d.setDate(d.getDate() + 1);
    this.irADia(d);
  }

  agendaHoy(): void {
    this.irADia(new Date());
  }

  etiquetaDiaAgenda(): string {
    const texto = this.agendaDia().toLocaleDateString('es-AR', { weekday: 'long', day: 'numeric', month: 'long' });
    return texto.charAt(0).toUpperCase() + texto.slice(1);
  }

  seleccionarTurno(t: Turno): void {
    this.turnoSeleccionadoId.set(this.turnoSeleccionadoId() === t.id ? null : t.id);
  }

  nombreServicio(servicioId: number | null): string {
    return this.servicios().find(s => s.id === servicioId)?.nombre ?? '';
  }

  nombreProfesional(profesionalId: number | null): string {
    return this.profesionales().find(p => p.id === profesionalId)?.nombre ?? 'Sin asignar';
  }

  // --- Inicio: la agenda de hoy y los pendientes salen de los turnos ya cargados; las
  // estadísticas por rango vienen de GET /comercios/{id}/resumen. Si esa llamada falla, los
  // bloques de estadísticas simplemente no se muestran. ---
  resumen = signal<Resumen | null>(null);
  cargandoResumen = signal(false);
  rangoResumen = signal<7 | 30 | 90>(30);
  readonly opcionesRangoResumen: (7 | 30 | 90)[] = [7, 30, 90];

  cargarResumen(): void {
    const hoy = new Date();
    const desde = new Date(hoy.getFullYear(), hoy.getMonth(), hoy.getDate() - (this.rangoResumen() - 1));
    this.cargandoResumen.set(true);
    this.api.getResumen(this.sesion.comercioId, this.formatearFecha(desde), this.formatearFecha(hoy)).subscribe({
      next: r => {
        this.resumen.set(r);
        this.cargandoResumen.set(false);
      },
      error: () => {
        this.resumen.set(null);
        this.cargandoResumen.set(false);
      }
    });
  }

  cambiarRangoResumen(dias: 7 | 30 | 90): void {
    if (this.rangoResumen() === dias) return;
    this.rangoResumen.set(dias);
    this.cargarResumen();
  }

  // Variación porcentual contra el período anterior del mismo largo; null si antes no hubo
  // nada (no tiene sentido un "+∞%").
  variacion(actual: number, anterior: number): number | null {
    if (anterior <= 0) return null;
    return Math.round(((actual - anterior) / anterior) * 100);
  }

  textoVariacion(actual: number, anterior: number): string {
    const v = this.variacion(actual, anterior) ?? 0;
    return `${v > 0 ? '+' : ''}${v}% vs. los ${this.rangoResumen()} días anteriores`;
  }

  porcentaje(parte: number, total: number): number {
    return total > 0 ? Math.min(100, Math.round((parte / total) * 100)) : 0;
  }

  horasTexto(minutos: number): string {
    const horas = minutos / 60;
    return `${Number.isInteger(horas) ? horas : horas.toFixed(1)} h`;
  }

  etiquetaFechaCorta(fecha: string): string {
    const [, m, dia] = fecha.split('-');
    return `${dia}/${m}`;
  }

  // Heatmap: lunes a domingo × las horas donde hubo turnos (como mínimo de 9 a 20).
  horasHeatmap(r: Resumen): number[] {
    const horas = r.horariosMasPedidos.map(c => c.hora);
    const desde = Math.min(9, ...horas);
    const hasta = Math.max(20, ...horas);
    return Array.from({ length: hasta - desde + 1 }, (_, i) => desde + i);
  }

  cantidadHeatmap(r: Resumen, diaSemana: number, hora: number): number {
    return r.horariosMasPedidos.find(c => c.diaSemana === diaSemana && c.hora === hora)?.cantidad ?? 0;
  }

  maxHeatmap(r: Resumen): number {
    return Math.max(1, ...r.horariosMasPedidos.map(c => c.cantidad));
  }

  turnosDeHoy(): Turno[] {
    return this.turnosDelDia(this.ahora()).filter(t => t.estadoReserva !== 3);
  }

  pendientesDeConfirmar(): Turno[] {
    return this.turnos()
      .filter(t => t.estadoReserva === 1 && !this.esperandoPagoMercadoPago(t))
      .sort((a, b) => this.vencimientoPreReserva(a).getTime() - this.vencimientoPreReserva(b).getTime());
  }

  // FechaCreacion se guarda en UTC; si viene sin zona (como la devuelve EF al leer de la
  // base), se le agrega la "Z" para que el navegador no la tome como hora local.
  private vencimientoPreReserva(t: Turno): Date {
    const iso = /Z$|[+-]\d{2}:\d{2}$/.test(t.fechaCreacion) ? t.fechaCreacion : `${t.fechaCreacion}Z`;
    return new Date(new Date(iso).getTime() + HORAS_LIMITE_PARA_CONFIRMAR * 60 * 60 * 1000);
  }

  minutosParaVencer(t: Turno): number {
    return Math.floor((this.vencimientoPreReserva(t).getTime() - this.ahora().getTime()) / 60000);
  }

  textoVencimiento(t: Turno): string {
    const minutos = this.minutosParaVencer(t);
    if (minutos <= 0) return 'Vencida';
    if (minutos < 60) return `Vence en ${minutos} min`;
    const horas = Math.floor(minutos / 60);
    const resto = minutos % 60;
    return resto === 0 ? `Vence en ${horas} h` : `Vence en ${horas} h ${resto} min`;
  }

  verTurnoEnCalendario(t: Turno): void {
    this.irADia(new Date(t.fechaHoraInicio));
    this.vistaTurnos.set('calendario');
    this.turnoSeleccionadoId.set(t.id);
    this.abrirTab('turnos');
  }

  // --- Carga manual de turno presencial ---
  nuevoTurnoServicioId = 0;
  nuevoTurnoProfesionalId: number | null = null;
  nuevoTurnoFecha = '';
  nuevoTurnoHora = '';
  nuevoTurnoClienteNombre = '';
  nuevoTurnoClienteWhatsApp = '';
  errorTurnoManual = signal<string | null>(null);
  cargandoTurnoManual = signal(false);

  // --- Historial (cortes realizados + control de ingresos) ---
  historial = signal<Historial | null>(null);
  cargandoHistorial = signal(false);
  mesHistorial = signal<{ anio: number; mes: number }>(this.mesActual());

  // --- Ganancias (exclusivo Premium) ---
  ganancias = signal<Ganancias | null>(null);
  cargandoGanancias = signal(false);
  errorGanancias = signal<string | null>(null);
  gananciasDesde = '';
  gananciasHasta = '';
  private gananciasCargadaAlMenosUnaVez = false;

  // --- WhatsApp (placeholder, exclusivo Premium) ---
  whatsappConfig = signal<WhatsAppConfig | null>(null);
  cargandoWhatsApp = signal(false);
  errorWhatsApp = signal<string | null>(null);
  private whatsAppCargadoAlMenosUnaVez = false;

  // --- Servicios ---
  servicios = signal<Servicio[]>([]);
  nuevoServicioNombre = '';
  nuevoServicioDuracion: number | null = null;
  nuevoServicioPrecio: number | null = null;
  nuevoServicioSenia: number | null = null;
  errorServicio = signal<string | null>(null);

  servicioEditandoId = signal<number | null>(null);
  editServicioNombre = '';
  editServicioDuracion: number | null = null;
  editServicioPrecio: number | null = null;
  editServicioSenia: number | null = null;
  errorEditServicio = signal<string | null>(null);

  // --- Horarios ---
  horarios = signal<Horario[]>([]);
  nuevoHorarioDia = 1;
  nuevoHorarioInicio = '10:00';
  nuevoHorarioFin = '19:00';

  horarioEditandoId = signal<number | null>(null);
  editHorarioDia = 1;
  editHorarioInicio = '10:00';
  editHorarioFin = '19:00';
  errorEditHorario = signal<string | null>(null);

  // --- Profesionales ---
  profesionales = signal<Profesional[]>([]);
  nuevoProfesionalNombre = '';
  nuevoProfesionalSucursalId: number | null = null;
  nuevoProfesionalEspecialidad = '';
  errorProfesional = signal<string | null>(null);
  profesionalSeleccionado = signal<Profesional | null>(null);
  horariosDelProfesional = signal<Horario[]>([]);
  profesionalEditandoId = signal<number | null>(null);
  editProfesionalNombre = '';
  editProfesionalSucursalId: number | null = null;
  editProfesionalEspecialidad = '';
  errorEditProfesional = signal<string | null>(null);
  subiendoFotoProfesionalId = signal<number | null>(null);

  // --- Sucursales ---
  sucursales = signal<Sucursal[]>([]);
  nuevaSucursalNombre = '';
  nuevaSucursalDireccion = '';
  nuevaSucursalTelefono = '';
  errorSucursal = signal<string | null>(null);
  sucursalEditandoId = signal<number | null>(null);
  editSucursalNombre = '';
  editSucursalDireccion = '';
  editSucursalTelefono = '';
  errorEditSucursal = signal<string | null>(null);

  constructor(private api: Api, private session: Session, private router: Router, private route: ActivatedRoute) {
    // El guard de la ruta ya garantiza que hay sesión antes de llegar acá.
    this.sesion = this.session.obtenerUsuario()!;

    const ahora = new Date();
    this.nuevoTurnoFecha = this.formatearFecha(ahora);
    this.nuevoTurnoHora = `${String(ahora.getHours()).padStart(2, '0')}:${String(ahora.getMinutes()).padStart(2, '0')}`;

    const inicioMes = new Date(ahora.getFullYear(), ahora.getMonth(), 1);
    this.gananciasDesde = this.formatearFecha(inicioMes);
    this.gananciasHasta = this.formatearFecha(ahora);

    this.planSeleccionado = this.sesion.planActual;
    this.cicloSeleccionado = this.sesion.cicloFacturacion;

    this.perfilNombre = this.sesion.nombre;
    this.perfilTelefono = this.sesion.telefonoNotificaciones;
    this.perfilDatosBancarios = this.sesion.datosBancarios;

    this.cargarTodo();

    // Mueve la línea "ahora" del calendario una vez por minuto.
    const reloj = setInterval(() => this.ahora.set(new Date()), 60_000);
    inject(DestroyRef).onDestroy(() => clearInterval(reloj));

    // Permite entrar directo a /panel?tab=ganancias, ?tab=whatsapp o ?tab=plan (ej. un link
    // guardado); si el comercio no es Premium, la pestaña igual se abre pero muestra el aviso.
    const tabPorUrl = this.route.snapshot.queryParamMap.get('tab');
    if (tabPorUrl === 'ganancias' || tabPorUrl === 'whatsapp' || tabPorUrl === 'plan' || tabPorUrl === 'perfil') this.abrirTab(tabPorUrl);
    this.procesarVueltaDeMercadoPago();
  }

  // Vueltas de Mercado Pago al panel: de conectar la cuenta (?mercadopago=conectado|error|vencido)
  // o de pagar el plan (?pagoPlan={id}&payment_id=...). Después se limpia la URL.
  private procesarVueltaDeMercadoPago(): void {
    const query = this.route.snapshot.queryParamMap;
    const resultadoConexion = query.get('mercadopago');
    const pagoPlanId = Number(query.get('pagoPlan'));
    if (!resultadoConexion && !pagoPlanId) return;

    if (resultadoConexion === 'conectado') this.avisoMercadoPago.set('¡Listo! Tu cuenta de Mercado Pago quedó conectada y tus clientes ya pueden pagar la seña por ahí.');
    else if (resultadoConexion === 'vencido') this.errorMercadoPago.set('Pasó demasiado tiempo para conectar la cuenta. Probá de nuevo.');
    else if (resultadoConexion) this.errorMercadoPago.set('No se pudo conectar tu cuenta de Mercado Pago. Probá de nuevo.');

    if (pagoPlanId) {
      const pagoMpTexto = query.get('payment_id') ?? query.get('collection_id');
      const pagoMpId = pagoMpTexto && /^\d+$/.test(pagoMpTexto) ? Number(pagoMpTexto) : null;
      this.avisoPagoPlan.set({ tipo: 'info', texto: 'Revisando tu pago en Mercado Pago...' });
      this.api.verificarPagoPlan(this.sesion.comercioId, pagoPlanId, pagoMpId).subscribe({
        next: r => {
          if (r.estado === 'Aprobado') {
            this.sesion = { ...this.sesion, planActual: r.planActual, cicloFacturacion: r.cicloFacturacion, fechaProximoPago: r.fechaProximoPago, activo: r.activo };
            this.session.iniciarSesion(this.sesion);
            this.planSeleccionado = r.planActual;
            this.cicloSeleccionado = r.cicloFacturacion;
            this.actualizarPrecioPlanPedido();
            this.cargarPagoRenovacion();
            this.avisoPagoPlan.set({ tipo: 'ok', texto: `¡Gracias! Recibimos tu pago: tu plan ${r.planActual} quedó renovado.` });
          } else if (r.estado === 'Rechazado') {
            this.avisoPagoPlan.set({ tipo: 'error', texto: 'Mercado Pago rechazó el pago. Probá con otro medio de pago.' });
          } else if (pagoMpId) {
            this.avisoPagoPlan.set({ tipo: 'info', texto: 'Tu pago está en proceso. Cuando Mercado Pago lo apruebe, tu plan se renueva solo.' });
          } else {
            this.avisoPagoPlan.set({ tipo: 'error', texto: 'El pago no se completó. Podés intentarlo de nuevo cuando quieras.' });
          }
        },
        error: () => this.avisoPagoPlan.set({ tipo: 'info', texto: 'No pudimos revisar tu pago ahora. Si se aprobó, tu plan se renueva solo en unos minutos.' })
      });
    }

    this.router.navigate([], { relativeTo: this.route, queryParams: { tab: this.tabActiva() }, replaceUrl: true });
  }

  esPremium(): boolean {
    return this.sesion.planActual === 'Premium';
  }

  // ================= RENOVACIÓN DEL PLAN =================
  diasParaRenovacion(): number | null {
    if (!this.sesion.fechaProximoPago) return null;
    const hoy = new Date();
    hoy.setHours(0, 0, 0, 0);
    const vencimiento = new Date(this.sesion.fechaProximoPago);
    vencimiento.setHours(0, 0, 0, 0);
    const msPorDia = 1000 * 60 * 60 * 24;
    return Math.round((vencimiento.getTime() - hoy.getTime()) / msPorDia);
  }

  // Para el anillo de Mi Plan: qué parte del ciclo (30 o 365 días) falta hasta la renovación.
  readonly circunferenciaAnillo = 2 * Math.PI * 52;

  progresoRenovacion(): number {
    const dias = this.diasParaRenovacion();
    if (dias === null) return 0;
    const largoCiclo = this.sesion.cicloFacturacion === 'Anual' ? 365 : 30;
    return Math.min(1, Math.max(0, dias / largoCiclo));
  }

  renovacionUrgente(): boolean {
    const dias = this.diasParaRenovacion();
    return dias !== null && dias <= 7;
  }

  // Monto de referencia según plan, ciclo y cantidad total de profesionales del comercio
  // (sumando todas sus sucursales). No es necesariamente lo que termina cobrándose si el
  // Super Admin acordó un monto puntual distinto, pero le da al cliente una cifra concreta
  // para coordinar la transferencia en vez de tener que calcularla él mismo.
  montoEstimadoPlan(): number {
    const tabla = this.sesion.cicloFacturacion === 'Anual' ? PRECIOS_PLAN_ANUAL : PRECIOS_PLAN;
    const precios = tabla[this.sesion.planActual] ?? tabla['Gratuito'];
    const cantidadProfesionales = this.profesionales().length;
    return cantidadProfesionales > 1 ? precios.porProfesional * cantidadProfesionales : precios.unico;
  }

  montoEstimadoTexto(): string {
    const monto = this.montoEstimadoPlan();
    if (monto <= 0) return 'a coordinar';
    const periodo = this.sesion.cicloFacturacion === 'Anual' ? '/año' : '/mes';
    return `$${monto.toLocaleString('es-AR')}${periodo}`;
  }

  linkPagoWhatsApp(): string {
    const mensaje = `Hola! Quiero renovar mi plan de Reserva2. Comercio: ${this.sesion.nombre}. Plan: ${this.sesion.planActual} (${this.sesion.cicloFacturacion}). Profesionales: ${this.profesionales().length}. Monto estimado: ${this.montoEstimadoTexto()}.`;
    return linkWhatsApp(mensaje);
  }

  linkAyudaWhatsApp(): string {
    return linkWhatsApp(`Hola! Tengo una duda usando Reserva2. Comercio: ${this.sesion.nombre}.`);
  }

  // Toda cuenta nueva entra pausada (sesion.activo = false): el dueño ya puede armar su
  // panel, pero su página pública no recibe reservas hasta que se lo confirme por acá y
  // el Super Admin lo active desde su panel.
  linkActivacionWhatsApp(): string {
    return linkWhatsApp(`Hola! Ya registré mi comercio en Reserva2 (${this.sesion.nombre} · ${this.sesion.aliasUrl}). ¿Podés activarme la cuenta?`);
  }

  linkPublicoTexto(): string {
    return `${window.location.host}/${this.sesion.aliasUrl}`;
  }

  copiarLink(): void {
    const url = this.linkPublicoTexto();
    navigator.clipboard?.writeText(url).then(() => {
      this.linkCopiado.set(true);
      setTimeout(() => this.linkCopiado.set(false), 2000);
    });
  }

  cerrarSesion(): void {
    this.session.cerrarSesion();
    this.router.navigateByUrl('/panel/login');
  }

  // ================= MENÚ LATERAL =================
  itemsMenu() {
    return MENU_PANEL.filter(i => !i.soloPremium || this.esPremium());
  }

  tituloTabActiva(): string {
    return MENU_PANEL.find(i => i.tab === this.tabActiva())?.etiqueta ?? '';
  }

  fechaHoyTexto(): string {
    const texto = new Date().toLocaleDateString('es-AR', { weekday: 'long', day: 'numeric', month: 'long' });
    return texto.charAt(0).toUpperCase() + texto.slice(1);
  }

  // Sale de los turnos ya cargados, no pega al backend.
  turnosSinConfirmar(): number {
    return this.turnos().filter(t => t.estadoReserva === 1 && !this.esperandoPagoMercadoPago(t)).length;
  }

  // Turnos que necesitan algo del comercio: pre-reservas viejas sin confirmar y señas a
  // verificar (la pastilla del menú cuenta los dos).
  turnosParaRevisar(): number {
    return this.turnosSinConfirmar() + this.seniasAVerificar().length;
  }

  // ================= SEÑA =================
  seniasAVerificar(): Turno[] {
    return this.turnos()
      .filter(t => t.estadoReserva !== 3 && t.señaVerificada === false && t.señaMedio !== 'MercadoPago')
      .sort((a, b) => a.fechaHoraInicio.localeCompare(b.fechaHoraInicio));
  }

  // Reserva con seña por Mercado Pago que el cliente todavía no pagó: no hay nada que
  // verificar ni confirmar, se confirma sola al aprobarse el pago o se libera si no paga.
  esperandoPagoMercadoPago(t: Turno): boolean {
    return t.señaMedio === 'MercadoPago' && t.señaVerificada === false && t.estadoReserva === 1;
  }

  textoSenia(t: Turno): string {
    if (t.señaMedio === 'MercadoPago') return t.señaVerificada ? 'Pagado con Mercado Pago' : 'Esperando el pago';
    return t.señaVerificada ? 'Seña verificada' : 'Seña a verificar';
  }

  montoSenia(t: Turno): number | null {
    return this.servicios().find(s => s.id === t.servicioId)?.montoSeña ?? null;
  }

  // Comprobante abierto en el visor (se pide con el token del comercio, no es un link público).
  comprobanteTurno = signal<Turno | null>(null);
  comprobanteUrl = signal<string | null>(null);
  cargandoComprobante = signal(false);
  errorComprobante = signal<string | null>(null);

  verComprobante(t: Turno): void {
    this.cerrarComprobante();
    this.comprobanteTurno.set(t);
    this.cargandoComprobante.set(true);
    this.api.getComprobanteTurno(t.id).subscribe({
      next: blob => {
        this.comprobanteUrl.set(URL.createObjectURL(blob));
        this.cargandoComprobante.set(false);
      },
      error: () => {
        this.cargandoComprobante.set(false);
        this.errorComprobante.set('No pudimos abrir el comprobante.');
      }
    });
  }

  cerrarComprobante(): void {
    const url = this.comprobanteUrl();
    if (url) URL.revokeObjectURL(url);
    this.comprobanteUrl.set(null);
    this.comprobanteTurno.set(null);
    this.errorComprobante.set(null);
  }

  marcarSeniaVerificada(t: Turno): void {
    this.api.marcarSeniaVerificada(t.id).subscribe(() => {
      if (this.comprobanteTurno()?.id === t.id) this.cerrarComprobante();
      this.cargarTurnos();
    });
  }

  cancelarDesdeComprobante(t: Turno): void {
    if (!confirm(`¿Cancelar el turno de ${t.clienteNombre}? El horario vuelve a quedar libre.`)) return;
    this.cerrarComprobante();
    this.cancelar(t);
  }

  inicialesComercio(): string {
    const palabras = this.sesion.nombre.trim().split(/\s+/).filter(Boolean);
    return palabras.slice(0, 2).map(p => p[0].toUpperCase()).join('') || 'R2';
  }

  abrirTab(tab: TabPanel): void {
    this.tabActiva.set(tab);
    if (tab === 'ganancias' && !this.gananciasCargadaAlMenosUnaVez) this.cargarGanancias();
    if (tab === 'whatsapp' && !this.whatsAppCargadoAlMenosUnaVez) this.cargarWhatsAppConfig();
    if (tab === 'plan') {
      this.actualizarPrecioPlanPedido();
      this.cargarPagoRenovacion();
    }
    if (tab === 'perfil' && !this.mercadoPagoCargadoAlMenosUnaVez) this.cargarMercadoPago();
  }

  // ================= MI PLAN =================
  onPlanPedidoCambio(): void {
    this.actualizarPrecioPlanPedido();
  }

  // Precio de lista del plan pedido (misma cuenta que el registro, vía /api/precio-plan),
  // con la cantidad real de profesionales y sucursales cargadas.
  actualizarPrecioPlanPedido(): void {
    this.cargarPagoPlanPedido();
    const idPedido = ++this.precioPlanRequestId;
    this.precioPlanPedido.set(null);
    this.api.getPrecioPlan(this.planSeleccionado, this.cicloSeleccionado,
      Math.max(1, this.profesionales().length), Math.max(1, this.sucursales().length)).subscribe({
      next: r => { if (idPedido === this.precioPlanRequestId) this.precioPlanPedido.set(r.precio); },
      error: () => { if (idPedido === this.precioPlanRequestId) this.precioPlanPedido.set(null); }
    });
  }

  // Si el backend ya devolvió lo que le corresponde pagar a este comercio (incluye el extra de
  // cobros online y el monto acordado), se muestra eso; si no, la tarifa de lista.
  precioPlanPedidoTexto(): string {
    const precio = this.pagoPlanPedido()?.monto ?? this.precioPlanPedido();
    if (precio === null) return 'a confirmar';
    if (precio <= 0) return 'Gratis';
    return `$${precio.toLocaleString('es-AR')}${this.cicloSeleccionado === 'Anual' ? '/año' : '/mes'}`;
  }

  cargarPagoRenovacion(): void {
    if (this.sesion.planActual === 'Gratuito') {
      this.pagoRenovacion.set(null);
      return;
    }
    this.api.getMontoPagoPlan(this.sesion.comercioId, this.sesion.planActual, this.sesion.cicloFacturacion).subscribe({
      next: r => this.pagoRenovacion.set(r),
      error: () => this.pagoRenovacion.set(null)
    });
  }

  private cargarPagoPlanPedido(): void {
    const idPedido = ++this.pagoPlanPedidoRequestId;
    this.pagoPlanPedido.set(null);
    if (!this.planSeleccionado || this.planSeleccionado === 'Gratuito') return;
    this.api.getMontoPagoPlan(this.sesion.comercioId, this.planSeleccionado, this.cicloSeleccionado).subscribe({
      next: r => { if (idPedido === this.pagoPlanPedidoRequestId) this.pagoPlanPedido.set(r); },
      error: () => { if (idPedido === this.pagoPlanPedidoRequestId) this.pagoPlanPedido.set(null); }
    });
  }

  textoMonto(monto: number, ciclo: string): string {
    return `$${monto.toLocaleString('es-AR')}${ciclo === 'Anual' ? '/año' : '/mes'}`;
  }

  // Lleva al checkout de Mercado Pago; el plan se renueva (o cambia) cuando se aprueba el pago.
  pagarPlanConMercadoPago(plan: string, ciclo: string): void {
    this.errorPagoPlan.set(null);
    this.iniciandoPagoPlan.set(true);
    this.api.pagarPlan(this.sesion.comercioId, plan, ciclo).subscribe({
      next: r => { window.location.href = r.urlPago; },
      error: err => {
        this.iniciandoPagoPlan.set(false);
        this.errorPagoPlan.set(err.error?.mensaje ?? 'No pudimos iniciar el pago. Probá de nuevo.');
      }
    });
  }

  linkAddonCobrosWhatsApp(): string {
    return linkWhatsApp(`Hola! Quiero sumar los cobros automáticos con Mercado Pago a mi plan de Reserva2. Comercio: ${this.sesion.nombre} (${this.linkPublicoTexto()}).`);
  }

  linkCambioPlanWhatsApp(): string {
    const mensaje = [
      'Hola! Quiero cambiar mi plan de Reserva2.',
      `Comercio: ${this.sesion.nombre}`,
      `Link: ${this.linkPublicoTexto()}`,
      `Plan actual: ${this.sesion.planActual} (${this.sesion.cicloFacturacion})`,
      `Plan pedido: ${this.planSeleccionado} (${this.cicloSeleccionado})`,
      `Profesionales: ${this.profesionales().length}`,
      `Sucursales: ${this.sucursales().length}`,
      `Precio del plan pedido: ${this.precioPlanPedidoTexto()}`
    ].join('\n');
    return linkWhatsApp(mensaje);
  }

  // ================= PERFIL DEL COMERCIO =================
  logoUrlCompleta(): string | null {
    return urlArchivo(this.sesion.logoUrl);
  }

  guardarPerfil(): void {
    this.errorPerfil.set(null);
    this.perfilGuardadoOk.set(false);

    if (!this.perfilNombre.trim()) {
      this.errorPerfil.set('El nombre del negocio no puede estar vacío.');
      return;
    }

    this.guardandoPerfil.set(true);
    this.api.actualizarPerfil(this.sesion.comercioId, {
      nombre: this.perfilNombre.trim(),
      telefonoNotificaciones: this.perfilTelefono.trim(),
      datosBancarios: this.perfilDatosBancarios.trim()
    }).subscribe({
      next: p => {
        this.guardandoPerfil.set(false);
        this.perfilGuardadoOk.set(true);
        this.sesion = { ...this.sesion, nombre: p.nombre, telefonoNotificaciones: p.telefonoNotificaciones, datosBancarios: p.datosBancarios };
        this.session.actualizarPerfilEnSesion(p.nombre, p.telefonoNotificaciones, p.datosBancarios, this.sesion.logoUrl);
      },
      error: err => {
        this.guardandoPerfil.set(false);
        this.errorPerfil.set(err.error?.mensaje ?? 'No pudimos actualizar tu perfil.');
      }
    });
  }

  cambiarPassword(): void {
    this.errorPassword.set(null);
    this.passwordCambiadaOk.set(false);

    if (!this.passwordActual || !this.passwordNueva) {
      this.errorPassword.set('Completá tu contraseña actual y la nueva.');
      return;
    }
    if (this.passwordNueva.length < 8) {
      this.errorPassword.set('La contraseña nueva tiene que tener al menos 8 caracteres.');
      return;
    }
    if (this.passwordNueva !== this.passwordNuevaRepetida) {
      this.errorPassword.set('Las contraseñas nuevas no coinciden.');
      return;
    }

    this.cambiandoPassword.set(true);
    this.api.cambiarPassword(this.sesion.comercioId, this.passwordActual, this.passwordNueva).subscribe({
      next: () => {
        this.cambiandoPassword.set(false);
        this.passwordCambiadaOk.set(true);
        this.passwordActual = '';
        this.passwordNueva = '';
        this.passwordNuevaRepetida = '';
      },
      error: err => {
        this.cambiandoPassword.set(false);
        this.errorPassword.set(err.error?.mensaje ?? 'No pudimos cambiar tu contraseña.');
      }
    });
  }

  subirLogo(evento: Event): void {
    const input = evento.target as HTMLInputElement;
    const archivo = input.files?.[0];
    if (!archivo) return;

    this.errorLogo.set(null);
    this.subiendoLogo.set(true);
    this.api.subirLogo(this.sesion.comercioId, archivo).subscribe({
      next: r => {
        this.subiendoLogo.set(false);
        this.sesion = { ...this.sesion, logoUrl: r.logoUrl };
        this.session.actualizarPerfilEnSesion(this.sesion.nombre, this.sesion.telefonoNotificaciones, this.sesion.datosBancarios, r.logoUrl);
      },
      error: err => {
        this.subiendoLogo.set(false);
        this.errorLogo.set(err.error?.mensaje ?? 'No pudimos subir el logo.');
      }
    });
    input.value = '';
  }

  private cargarTodo(): void {
    this.cargarTurnos();
    this.cargarServicios();
    this.cargarHorarios();
    this.cargarProfesionales();
    this.cargarSucursales();
    this.cargarHistorial();
    this.cargarResumen();
  }

  private mesActual(): { anio: number; mes: number } {
    const ahora = new Date();
    return { anio: ahora.getFullYear(), mes: ahora.getMonth() + 1 };
  }

  private formatearFecha(d: Date): string {
    return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
  }

  // ================= TURNOS =================
  cargarTurnos(): void {
    this.api.getTurnosDeComercio(this.sesion.comercioId).subscribe(turnos => this.turnos.set(turnos));
  }

  confirmar(t: Turno): void {
    this.api.confirmarTurno(t.id).subscribe(() => this.cargarTurnos());
  }

  cancelar(t: Turno): void {
    this.api.cancelarTurno(t.id).subscribe(() => this.cargarTurnos());
  }

  etiquetaEstado(estado: number): string {
    return estado === 1 ? 'Pre-reservado' : estado === 2 ? 'Confirmado' : 'Cancelado';
  }

  // ================= TURNO PRESENCIAL (carga manual) =================
  agregarTurnoManual(): void {
    this.errorTurnoManual.set(null);

    if (!this.nuevoTurnoServicioId) {
      this.errorTurnoManual.set('Elegí un servicio.');
      return;
    }
    if (!this.nuevoTurnoClienteNombre.trim()) {
      this.errorTurnoManual.set('Ingresá el nombre del cliente.');
      return;
    }
    if (!this.nuevoTurnoFecha || !this.nuevoTurnoHora) {
      this.errorTurnoManual.set('Elegí la fecha y la hora del turno.');
      return;
    }

    this.cargandoTurnoManual.set(true);
    this.api.crearTurnoManual(this.sesion.comercioId, {
      servicioId: this.nuevoTurnoServicioId,
      fechaHoraInicio: `${this.nuevoTurnoFecha}T${this.nuevoTurnoHora}:00`,
      clienteNombre: this.nuevoTurnoClienteNombre.trim(),
      clienteWhatsApp: this.nuevoTurnoClienteWhatsApp.trim(),
      profesionalId: this.nuevoTurnoProfesionalId ?? undefined
    }).subscribe({
      next: () => {
        this.cargandoTurnoManual.set(false);
        this.nuevoTurnoClienteNombre = '';
        this.nuevoTurnoClienteWhatsApp = '';
        this.cargarTurnos();
        this.cargarHistorial();
      },
      error: err => {
        this.cargandoTurnoManual.set(false);
        this.errorTurnoManual.set(err.error?.mensaje ?? 'No pudimos cargar el turno.');
      }
    });
  }

  // ================= HISTORIAL (cortes realizados + ingresos) =================
  cargarHistorial(): void {
    const { anio, mes } = this.mesHistorial();
    this.cargandoHistorial.set(true);
    this.api.getHistorial(this.sesion.comercioId, anio, mes).subscribe({
      next: h => {
        this.historial.set(h);
        this.cargandoHistorial.set(false);
      },
      error: () => this.cargandoHistorial.set(false)
    });
  }

  mesHistorialAnterior(): void {
    const { anio, mes } = this.mesHistorial();
    this.mesHistorial.set(mes === 1 ? { anio: anio - 1, mes: 12 } : { anio, mes: mes - 1 });
    this.cargarHistorial();
  }

  mesHistorialSiguiente(): void {
    const { anio, mes } = this.mesHistorial();
    this.mesHistorial.set(mes === 12 ? { anio: anio + 1, mes: 1 } : { anio, mes: mes + 1 });
    this.cargarHistorial();
  }

  // ================= GANANCIAS (exclusivo Premium) =================
  cargarGanancias(): void {
    if (!this.gananciasDesde || !this.gananciasHasta) return;

    this.gananciasCargadaAlMenosUnaVez = true;
    this.errorGanancias.set(null);
    this.cargandoGanancias.set(true);
    this.api.getGanancias(this.sesion.comercioId, this.gananciasDesde, this.gananciasHasta).subscribe({
      next: g => {
        this.ganancias.set(g);
        this.cargandoGanancias.set(false);
      },
      error: err => {
        this.cargandoGanancias.set(false);
        this.errorGanancias.set(err.error?.mensaje ?? 'No pudimos cargar el reporte de ganancias.');
      }
    });
  }

  alturaBarraFacturacion<T>(valor: number, serie: T[], campo: keyof T = 'total' as keyof T): number {
    const max = Math.max(1, ...serie.map(d => Number(d[campo])));
    return Math.max(4, Math.round((valor / max) * 100));
  }

  etiquetaDiaFacturacion(fecha: string): string {
    const [y, m, d] = fecha.split('-').map(Number);
    return this.dias[new Date(y, m - 1, d).getDay()].substring(0, 3);
  }

  // ================= WHATSAPP (placeholder, exclusivo Premium) =================
  // ================= MERCADO PAGO (señas) =================
  cargarMercadoPago(): void {
    this.mercadoPagoCargadoAlMenosUnaVez = true;
    this.cargandoMercadoPago.set(true);
    this.api.getMercadoPagoConfig(this.sesion.comercioId).subscribe({
      next: c => {
        this.mercadoPago.set(c);
        this.cargandoMercadoPago.set(false);
      },
      error: err => {
        this.cargandoMercadoPago.set(false);
        this.errorMercadoPago.set(err.error?.mensaje ?? 'No pudimos cargar tu configuración de Mercado Pago.');
      }
    });
  }

  conectarMercadoPago(): void {
    this.errorMercadoPago.set(null);
    this.cargandoMercadoPago.set(true);
    this.api.conectarMercadoPago(this.sesion.comercioId).subscribe({
      next: r => { window.location.href = r.url; },
      error: err => {
        this.cargandoMercadoPago.set(false);
        this.errorMercadoPago.set(err.error?.mensaje ?? 'No pudimos conectar con Mercado Pago. Probá de nuevo.');
      }
    });
  }

  cambiarMediosSenia(señaPorMercadoPago: boolean, señaPorTransferencia: boolean, cobroTotal = this.mercadoPago()?.cobroTotal ?? false): void {
    this.errorMercadoPago.set(null);
    this.avisoMercadoPago.set(null);
    this.cargandoMercadoPago.set(true);
    this.api.actualizarMercadoPagoConfig(this.sesion.comercioId, señaPorMercadoPago, señaPorTransferencia, cobroTotal).subscribe({
      next: c => {
        this.mercadoPago.set(c);
        this.cargandoMercadoPago.set(false);
      },
      error: err => {
        this.cargandoMercadoPago.set(false);
        this.errorMercadoPago.set(err.error?.mensaje ?? 'No pudimos guardar el cambio.');
      }
    });
  }

  desconectarMercadoPago(): void {
    if (!confirm('¿Desconectar tu cuenta de Mercado Pago? Tus clientes van a pagar la seña solo por transferencia.')) return;
    this.errorMercadoPago.set(null);
    this.avisoMercadoPago.set(null);
    this.cargandoMercadoPago.set(true);
    this.api.desconectarMercadoPago(this.sesion.comercioId).subscribe({
      next: c => {
        this.mercadoPago.set(c);
        this.cargandoMercadoPago.set(false);
      },
      error: err => {
        this.cargandoMercadoPago.set(false);
        this.errorMercadoPago.set(err.error?.mensaje ?? 'No pudimos desconectar la cuenta.');
      }
    });
  }

  cargarWhatsAppConfig(): void {
    this.whatsAppCargadoAlMenosUnaVez = true;
    this.errorWhatsApp.set(null);
    this.cargandoWhatsApp.set(true);
    this.api.getWhatsAppConfig(this.sesion.comercioId).subscribe({
      next: c => {
        this.whatsappConfig.set(c);
        this.cargandoWhatsApp.set(false);
      },
      error: err => {
        this.cargandoWhatsApp.set(false);
        this.errorWhatsApp.set(err.error?.mensaje ?? 'No pudimos cargar la configuración de WhatsApp.');
      }
    });
  }

  toggleWhatsApp(): void {
    const actual = this.whatsappConfig();
    if (!actual) return;

    this.cargandoWhatsApp.set(true);
    this.api.actualizarWhatsAppConfig(this.sesion.comercioId, !actual.activado).subscribe({
      next: c => {
        this.whatsappConfig.set(c);
        this.cargandoWhatsApp.set(false);
      },
      error: () => this.cargandoWhatsApp.set(false)
    });
  }

  // ================= SERVICIOS =================
  cargarServicios(): void {
    this.api.getServiciosPorComercio(this.sesion.comercioId).subscribe(servicios => {
      this.servicios.set(servicios);
      if (!this.nuevoTurnoServicioId && servicios.length > 0) this.nuevoTurnoServicioId = servicios[0].id;
    });
  }

  agregarServicio(): void {
    this.errorServicio.set(null);

    if (!this.nuevoServicioNombre.trim()) {
      this.errorServicio.set('Ingresá el nombre del servicio.');
      return;
    }
    if (!this.nuevoServicioDuracion || this.nuevoServicioDuracion <= 0) {
      this.errorServicio.set('Ingresá la duración en minutos.');
      return;
    }

    this.api.crearServicio({
      comercioId: this.sesion.comercioId,
      nombre: this.nuevoServicioNombre.trim(),
      duracionMinutos: this.nuevoServicioDuracion,
      precio: this.nuevoServicioPrecio ?? 0,
      montoSeña: this.nuevoServicioSenia,
      activo: true
    }).subscribe(() => {
      this.nuevoServicioNombre = '';
      this.nuevoServicioDuracion = null;
      this.nuevoServicioPrecio = null;
      this.nuevoServicioSenia = null;
      this.cargarServicios();
    });
  }

  iniciarEdicionServicio(s: Servicio): void {
    this.servicioEditandoId.set(s.id);
    this.editServicioNombre = s.nombre;
    this.editServicioDuracion = s.duracionMinutos;
    this.editServicioPrecio = s.precio;
    this.editServicioSenia = s.montoSeña;
    this.errorEditServicio.set(null);
  }

  cancelarEdicionServicio(): void {
    this.servicioEditandoId.set(null);
  }

  guardarEdicionServicio(s: Servicio): void {
    this.errorEditServicio.set(null);

    if (!this.editServicioNombre.trim()) {
      this.errorEditServicio.set('Ingresá el nombre del servicio.');
      return;
    }
    if (!this.editServicioDuracion || this.editServicioDuracion <= 0) {
      this.errorEditServicio.set('Ingresá la duración en minutos.');
      return;
    }

    this.api.editarServicio(s.id, {
      nombre: this.editServicioNombre.trim(),
      duracionMinutos: this.editServicioDuracion,
      precio: this.editServicioPrecio ?? 0,
      montoSeña: this.editServicioSenia
    }).subscribe({
      next: () => {
        this.servicioEditandoId.set(null);
        this.cargarServicios();
      },
      error: err => this.errorEditServicio.set(err.error?.mensaje ?? 'No pudimos guardar los cambios.')
    });
  }

  quitarServicio(s: Servicio): void {
    this.api.eliminarServicio(s.id).subscribe(() => this.cargarServicios());
  }

  // ================= HORARIOS =================
  cargarHorarios(): void {
    this.api.getHorarios(this.sesion.comercioId).subscribe(horarios => this.horarios.set(horarios));
  }

  // Vista semanal de Horarios: de lunes a domingo, con una barra de 06h a 24h por día.
  readonly diasSemanaOrden = [1, 2, 3, 4, 5, 6, 0];
  readonly marcasBarraHorario = [6, 12, 18, 24];

  horariosDelDiaSemana(dia: number): Horario[] {
    return this.horarios()
      .filter(h => h.diaSemana === dia)
      .sort((a, b) => a.horaInicio.localeCompare(b.horaInicio));
  }

  segmentoBarraHorario(h: Horario): { izquierda: number; ancho: number } {
    const inicioBarra = 6 * 60;
    const largoBarra = 18 * 60;
    const ini = Math.min(Math.max(this.horaAMinutos(h.horaInicio), inicioBarra), 24 * 60);
    const finCrudo = this.horaAMinutos(h.horaFin);
    const fin = Math.min(Math.max(finCrudo === 0 ? 24 * 60 : finCrudo, inicioBarra), 24 * 60);
    return { izquierda: (ini - inicioBarra) / largoBarra * 100, ancho: Math.max(0, fin - ini) / largoBarra * 100 };
  }

  agregarHorario(): void {
    this.api.crearHorario(this.sesion.comercioId, {
      diaSemana: this.nuevoHorarioDia,
      horaInicio: this.nuevoHorarioInicio,
      horaFin: this.nuevoHorarioFin
    }).subscribe(() => this.cargarHorarios());
  }

  iniciarEdicionHorario(h: Horario): void {
    this.horarioEditandoId.set(h.id);
    this.editHorarioDia = h.diaSemana;
    this.editHorarioInicio = h.horaInicio.substring(0, 5);
    this.editHorarioFin = h.horaFin.substring(0, 5);
    this.errorEditHorario.set(null);
  }

  cancelarEdicionHorario(): void {
    this.horarioEditandoId.set(null);
  }

  guardarEdicionHorario(h: Horario): void {
    this.errorEditHorario.set(null);
    this.api.editarHorario(h.id, {
      diaSemana: this.editHorarioDia,
      horaInicio: this.editHorarioInicio,
      horaFin: this.editHorarioFin,
      profesionalId: h.profesionalId ?? undefined
    }).subscribe({
      next: () => {
        this.horarioEditandoId.set(null);
        const p = this.profesionalSeleccionado();
        if (h.profesionalId && p && p.id === h.profesionalId) {
          this.seleccionarProfesionalDeNuevo(p);
        } else {
          this.cargarHorarios();
        }
      },
      error: err => this.errorEditHorario.set(err.error?.mensaje ?? 'No pudimos guardar los cambios.')
    });
  }

  quitarHorario(h: Horario): void {
    this.api.eliminarHorario(h.id).subscribe(() => this.cargarHorarios());
  }

  // ================= PROFESIONALES =================
  cargarProfesionales(): void {
    this.api.getProfesionales(this.sesion.comercioId).subscribe(profesionales => this.profesionales.set(profesionales));
  }

  // ================= SUCURSALES =================
  nombreSucursal(sucursalId: number): string {
    return this.sucursales().find(s => s.id === sucursalId)?.nombre ?? '';
  }

  cargarSucursales(): void {
    this.api.getSucursales(this.sesion.comercioId).subscribe(sucursales => this.sucursales.set(sucursales));
  }

  agregarSucursal(): void {
    if (!this.nuevaSucursalNombre.trim()) return;

    this.errorSucursal.set(null);
    this.api.crearSucursal(this.sesion.comercioId, {
      nombre: this.nuevaSucursalNombre.trim(),
      direccion: this.nuevaSucursalDireccion.trim(),
      telefono: this.nuevaSucursalTelefono.trim() || null
    }).subscribe({
      next: () => {
        this.nuevaSucursalNombre = '';
        this.nuevaSucursalDireccion = '';
        this.nuevaSucursalTelefono = '';
        this.cargarSucursales();
      },
      error: err => this.errorSucursal.set(err.error?.mensaje ?? 'No pudimos agregar la sucursal.')
    });
  }

  iniciarEdicionSucursal(s: Sucursal): void {
    this.sucursalEditandoId.set(s.id);
    this.editSucursalNombre = s.nombre;
    this.editSucursalDireccion = s.direccion;
    this.editSucursalTelefono = s.telefono ?? '';
    this.errorEditSucursal.set(null);
  }

  cancelarEdicionSucursal(): void {
    this.sucursalEditandoId.set(null);
  }

  guardarEdicionSucursal(s: Sucursal): void {
    this.errorEditSucursal.set(null);

    if (!this.editSucursalNombre.trim()) {
      this.errorEditSucursal.set('Ingresá el nombre de la sucursal.');
      return;
    }

    this.api.editarSucursal(s.id, {
      nombre: this.editSucursalNombre.trim(),
      direccion: this.editSucursalDireccion.trim(),
      telefono: this.editSucursalTelefono.trim() || null,
      activa: s.activa
    }).subscribe({
      next: () => {
        this.sucursalEditandoId.set(null);
        this.cargarSucursales();
      },
      error: err => this.errorEditSucursal.set(err.error?.mensaje ?? 'No pudimos guardar los cambios.')
    });
  }

  toggleActivaSucursal(s: Sucursal): void {
    this.api.editarSucursal(s.id, { nombre: s.nombre, direccion: s.direccion, telefono: s.telefono, activa: !s.activa }).subscribe(() => {
      this.cargarSucursales();
    });
  }

  quitarSucursal(s: Sucursal): void {
    this.api.eliminarSucursal(s.id).subscribe(() => {
      this.cargarSucursales();
      this.cargarProfesionales();
    });
  }

  agregarProfesional(): void {
    if (!this.nuevoProfesionalNombre.trim()) return;

    this.errorProfesional.set(null);
    this.api.crearProfesional(this.sesion.comercioId, this.nuevoProfesionalNombre.trim(), this.nuevoProfesionalSucursalId,
      this.nuevoProfesionalEspecialidad.trim() || null).subscribe({
      next: () => {
        this.nuevoProfesionalNombre = '';
        this.nuevoProfesionalSucursalId = null;
        this.nuevoProfesionalEspecialidad = '';
        this.cargarProfesionales();
      },
      error: err => {
        this.errorProfesional.set(err.error?.mensaje ?? 'No pudimos agregar el profesional.');
      }
    });
  }

  iniciarEdicionProfesional(p: Profesional): void {
    this.profesionalEditandoId.set(p.id);
    this.editProfesionalNombre = p.nombre;
    this.editProfesionalSucursalId = p.sucursalId;
    this.editProfesionalEspecialidad = p.especialidad ?? '';
    this.errorEditProfesional.set(null);
  }

  cancelarEdicionProfesional(): void {
    this.profesionalEditandoId.set(null);
  }

  guardarEdicionProfesional(p: Profesional): void {
    this.errorEditProfesional.set(null);

    if (!this.editProfesionalNombre.trim()) {
      this.errorEditProfesional.set('Ingresá el nombre del profesional.');
      return;
    }

    this.api.editarProfesional(p.id, this.editProfesionalNombre.trim(), this.editProfesionalSucursalId,
      this.editProfesionalEspecialidad.trim() || null).subscribe({
      next: () => {
        this.profesionalEditandoId.set(null);
        this.cargarProfesionales();
      },
      error: err => this.errorEditProfesional.set(err.error?.mensaje ?? 'No pudimos guardar los cambios.')
    });
  }

  fotoProfesional(p: Profesional): string | null {
    return urlArchivo(p.fotoUrl);
  }

  // La foto se guarda apenas se elige (igual que el logo), sin esperar al botón Guardar.
  subirFotoProfesional(p: Profesional, evento: Event): void {
    const input = evento.target as HTMLInputElement;
    const archivo = input.files?.[0];
    input.value = '';
    if (!archivo) return;

    this.errorEditProfesional.set(null);
    this.subiendoFotoProfesionalId.set(p.id);
    this.api.subirFotoProfesional(p.id, archivo).subscribe({
      next: r => {
        this.subiendoFotoProfesionalId.set(null);
        this.profesionales.update(lista => lista.map(x => x.id === p.id ? { ...x, fotoUrl: r.fotoUrl } : x));
      },
      error: err => {
        this.subiendoFotoProfesionalId.set(null);
        this.errorEditProfesional.set(err.error?.mensaje ?? 'No pudimos subir la foto.');
      }
    });
  }

  quitarFotoProfesional(p: Profesional): void {
    this.errorEditProfesional.set(null);
    this.api.quitarFotoProfesional(p.id).subscribe({
      next: () => this.profesionales.update(lista => lista.map(x => x.id === p.id ? { ...x, fotoUrl: null } : x)),
      error: err => this.errorEditProfesional.set(err.error?.mensaje ?? 'No pudimos quitar la foto.')
    });
  }

  quitarProfesional(p: Profesional): void {
    this.api.eliminarProfesional(p.id).subscribe(() => {
      if (this.profesionalSeleccionado()?.id === p.id) this.cerrarHorariosDeProfesional();
      this.cargarProfesionales();
    });
  }

  seleccionarProfesional(p: Profesional): void {
    if (this.profesionalSeleccionado()?.id === p.id) {
      this.cerrarHorariosDeProfesional();
      return;
    }
    this.profesionalSeleccionado.set(p);
    this.api.getHorarios(p.comercioId, p.id).subscribe(horarios => this.horariosDelProfesional.set(horarios));
  }

  private cerrarHorariosDeProfesional(): void {
    this.profesionalSeleccionado.set(null);
    this.horariosDelProfesional.set([]);
  }

  agregarHorarioProfesional(): void {
    const p = this.profesionalSeleccionado();
    if (!p) return;

    this.api.crearHorario(this.sesion.comercioId, {
      diaSemana: this.nuevoHorarioDia,
      horaInicio: this.nuevoHorarioInicio,
      horaFin: this.nuevoHorarioFin,
      profesionalId: p.id
    }).subscribe(() => this.seleccionarProfesionalDeNuevo(p));
  }

  quitarHorarioProfesional(h: Horario): void {
    const p = this.profesionalSeleccionado();
    if (!p) return;
    this.api.eliminarHorario(h.id).subscribe(() => this.seleccionarProfesionalDeNuevo(p));
  }

  private seleccionarProfesionalDeNuevo(p: Profesional): void {
    this.api.getHorarios(p.comercioId, p.id).subscribe(horarios => this.horariosDelProfesional.set(horarios));
  }
}
