import { Component, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';

import { Api, Turno, Servicio, Horario, Profesional, Sucursal, LoginResponse, Historial, Ganancias, WhatsAppConfig, urlArchivo } from '../../core/api';
import { Session } from '../../core/session';
import { linkWhatsApp } from '../../core/whatsapp';
import { PlanSelector } from '../../shared/plan-selector/plan-selector';

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
  Premium: { unico: 11000, porProfesional: 8500 }
};
const PRECIOS_PLAN_ANUAL: Record<string, { unico: number; porProfesional: number }> = {
  Gratuito: { unico: 0, porProfesional: 0 },
  Basico: { unico: 63000, porProfesional: 43200 },
  Premium: { unico: 99000, porProfesional: 76500 }
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
  tabActiva = signal<'turnos' | 'servicios' | 'horarios' | 'profesionales' | 'sucursales' | 'historial' | 'ganancias' | 'whatsapp' | 'plan' | 'perfil'>('turnos');

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
  planSeleccionado = '';
  cicloSeleccionado = '';
  guardandoPlan = signal(false);
  errorPlan = signal<string | null>(null);
  planGuardadoOk = signal(false);

  // --- Turnos ---
  turnos = signal<Turno[]>([]);
  linkCopiado = signal(false);

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
  errorProfesional = signal<string | null>(null);
  profesionalSeleccionado = signal<Profesional | null>(null);
  horariosDelProfesional = signal<Horario[]>([]);
  profesionalEditandoId = signal<number | null>(null);
  editProfesionalNombre = '';
  editProfesionalSucursalId: number | null = null;
  errorEditProfesional = signal<string | null>(null);

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

    // Permite entrar directo a /panel?tab=ganancias, ?tab=whatsapp o ?tab=plan (ej. un link
    // guardado); si el comercio no es Premium, la pestaña igual se abre pero muestra el aviso.
    const tabPorUrl = this.route.snapshot.queryParamMap.get('tab');
    if (tabPorUrl === 'ganancias' || tabPorUrl === 'whatsapp' || tabPorUrl === 'plan') this.abrirTab(tabPorUrl);
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

  copiarLink(): void {
    const url = `${window.location.host}/${this.sesion.aliasUrl}`;
    navigator.clipboard?.writeText(url).then(() => {
      this.linkCopiado.set(true);
      setTimeout(() => this.linkCopiado.set(false), 2000);
    });
  }

  cerrarSesion(): void {
    this.session.cerrarSesion();
    this.router.navigateByUrl('/panel/login');
  }

  abrirTab(tab: 'turnos' | 'servicios' | 'horarios' | 'profesionales' | 'sucursales' | 'historial' | 'ganancias' | 'whatsapp' | 'plan' | 'perfil'): void {
    this.tabActiva.set(tab);
    if (tab === 'ganancias' && !this.gananciasCargadaAlMenosUnaVez) this.cargarGanancias();
    if (tab === 'whatsapp' && !this.whatsAppCargadoAlMenosUnaVez) this.cargarWhatsAppConfig();
  }

  // ================= MI PLAN =================
  guardarPlan(): void {
    this.errorPlan.set(null);
    this.planGuardadoOk.set(false);
    this.guardandoPlan.set(true);
    this.api.actualizarMiPlan(this.sesion.comercioId, this.planSeleccionado, this.cicloSeleccionado).subscribe({
      next: r => {
        this.guardandoPlan.set(false);
        this.planGuardadoOk.set(true);
        this.sesion = { ...this.sesion, planActual: r.planActual, cicloFacturacion: r.cicloFacturacion, fechaProximoPago: r.fechaProximoPago };
        this.session.actualizarPlanEnSesion(r.planActual, r.cicloFacturacion, r.fechaProximoPago);
      },
      error: err => {
        this.guardandoPlan.set(false);
        this.errorPlan.set(err.error?.mensaje ?? 'No pudimos actualizar tu plan.');
      }
    });
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
    if (this.passwordNueva.length < 6) {
      this.errorPassword.set('La contraseña nueva tiene que tener al menos 6 caracteres.');
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

  // ================= WHATSAPP (placeholder, exclusivo Premium) =================
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
    this.api.crearProfesional(this.sesion.comercioId, this.nuevoProfesionalNombre.trim(), this.nuevoProfesionalSucursalId).subscribe({
      next: () => {
        this.nuevoProfesionalNombre = '';
        this.nuevoProfesionalSucursalId = null;
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

    this.api.editarProfesional(p.id, this.editProfesionalNombre.trim(), this.editProfesionalSucursalId).subscribe({
      next: () => {
        this.profesionalEditandoId.set(null);
        this.cargarProfesionales();
      },
      error: err => this.errorEditProfesional.set(err.error?.mensaje ?? 'No pudimos guardar los cambios.')
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
