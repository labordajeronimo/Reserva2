import { Component, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';

import { Api, Turno, Servicio, Horario, Profesional, LoginResponse, Historial, Ganancias, WhatsAppConfig, urlArchivo } from '../../core/api';
import { Session } from '../../core/session';
import { PlanSelector } from '../../shared/plan-selector/plan-selector';

const DIAS = ['Domingo', 'Lunes', 'Martes', 'Miércoles', 'Jueves', 'Viernes', 'Sábado'];
const MESES = ['Enero', 'Febrero', 'Marzo', 'Abril', 'Mayo', 'Junio', 'Julio', 'Agosto', 'Septiembre', 'Octubre', 'Noviembre', 'Diciembre'];

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
  tabActiva = signal<'turnos' | 'servicios' | 'horarios' | 'profesionales' | 'historial' | 'ganancias' | 'whatsapp' | 'plan' | 'perfil'>('turnos');

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
  errorProfesional = signal<string | null>(null);
  profesionalSeleccionado = signal<Profesional | null>(null);
  horariosDelProfesional = signal<Horario[]>([]);
  profesionalEditandoId = signal<number | null>(null);
  editProfesionalNombre = '';
  errorEditProfesional = signal<string | null>(null);

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

  abrirTab(tab: 'turnos' | 'servicios' | 'horarios' | 'profesionales' | 'historial' | 'ganancias' | 'whatsapp' | 'plan' | 'perfil'): void {
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
        this.sesion = { ...this.sesion, planActual: r.planActual, cicloFacturacion: r.cicloFacturacion };
        this.session.actualizarPlanEnSesion(r.planActual, r.cicloFacturacion);
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

  agregarProfesional(): void {
    if (!this.nuevoProfesionalNombre.trim()) return;

    this.errorProfesional.set(null);
    this.api.crearProfesional(this.sesion.comercioId, this.nuevoProfesionalNombre.trim()).subscribe({
      next: () => {
        this.nuevoProfesionalNombre = '';
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

    this.api.editarProfesional(p.id, this.editProfesionalNombre.trim()).subscribe({
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
