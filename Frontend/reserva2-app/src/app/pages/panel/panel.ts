import { Component, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';

import { Api, Turno, Servicio, Horario, Profesional, LoginResponse, Historial, Ganancias, WhatsAppConfig } from '../../core/api';
import { Session } from '../../core/session';

const DIAS = ['Domingo', 'Lunes', 'Martes', 'Miércoles', 'Jueves', 'Viernes', 'Sábado'];
const MESES = ['Enero', 'Febrero', 'Marzo', 'Abril', 'Mayo', 'Junio', 'Julio', 'Agosto', 'Septiembre', 'Octubre', 'Noviembre', 'Diciembre'];

@Component({
  selector: 'app-panel',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  templateUrl: './panel.html',
  styleUrls: ['./panel.css']
})
export class Panel {
  dias = DIAS;
  meses = MESES;

  sesion: LoginResponse;
  tabActiva = signal<'turnos' | 'servicios' | 'horarios' | 'profesionales' | 'historial' | 'ganancias' | 'whatsapp'>('turnos');

  // --- Turnos ---
  turnos = signal<Turno[]>([]);

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
  nuevoServicioDuracion = 30;
  nuevoServicioPrecio = 0;
  nuevoServicioSenia = 0;

  // --- Horarios ---
  horarios = signal<Horario[]>([]);
  nuevoHorarioDia = 1;
  nuevoHorarioInicio = '10:00';
  nuevoHorarioFin = '19:00';

  // --- Profesionales ---
  profesionales = signal<Profesional[]>([]);
  nuevoProfesionalNombre = '';
  errorProfesional = signal<string | null>(null);
  profesionalSeleccionado = signal<Profesional | null>(null);
  horariosDelProfesional = signal<Horario[]>([]);

  constructor(private api: Api, private session: Session, private router: Router, private route: ActivatedRoute) {
    // El guard de la ruta ya garantiza que hay sesión antes de llegar acá.
    this.sesion = this.session.obtenerUsuario()!;

    const ahora = new Date();
    this.nuevoTurnoFecha = this.formatearFecha(ahora);
    this.nuevoTurnoHora = `${String(ahora.getHours()).padStart(2, '0')}:${String(ahora.getMinutes()).padStart(2, '0')}`;

    const inicioMes = new Date(ahora.getFullYear(), ahora.getMonth(), 1);
    this.gananciasDesde = this.formatearFecha(inicioMes);
    this.gananciasHasta = this.formatearFecha(ahora);

    this.cargarTodo();

    // Permite entrar directo a /panel?tab=ganancias o ?tab=whatsapp (ej. un link guardado);
    // si el comercio no es Premium, la pestaña igual se abre pero muestra el aviso del plan.
    const tabPorUrl = this.route.snapshot.queryParamMap.get('tab');
    if (tabPorUrl === 'ganancias' || tabPorUrl === 'whatsapp') this.abrirTab(tabPorUrl);
  }

  esPremium(): boolean {
    return this.sesion.planActual === 'Premium';
  }

  cerrarSesion(): void {
    this.session.cerrarSesion();
    this.router.navigateByUrl('/panel/login');
  }

  abrirTab(tab: 'turnos' | 'servicios' | 'horarios' | 'profesionales' | 'historial' | 'ganancias' | 'whatsapp'): void {
    this.tabActiva.set(tab);
    if (tab === 'ganancias' && !this.gananciasCargadaAlMenosUnaVez) this.cargarGanancias();
    if (tab === 'whatsapp' && !this.whatsAppCargadoAlMenosUnaVez) this.cargarWhatsAppConfig();
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
    if (!this.nuevoServicioNombre.trim()) return;

    this.api.crearServicio({
      comercioId: this.sesion.comercioId,
      nombre: this.nuevoServicioNombre.trim(),
      duracionMinutos: this.nuevoServicioDuracion,
      precio: this.nuevoServicioPrecio,
      montoSeña: this.nuevoServicioSenia,
      activo: true
    }).subscribe(() => {
      this.nuevoServicioNombre = '';
      this.nuevoServicioDuracion = 30;
      this.nuevoServicioPrecio = 0;
      this.nuevoServicioSenia = 0;
      this.cargarServicios();
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
