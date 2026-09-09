import { Component, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';

import { Api, Turno, Servicio, Horario } from '../../core/api';
import { Session } from '../../core/session';

const DIAS = ['Domingo', 'Lunes', 'Martes', 'Miércoles', 'Jueves', 'Viernes', 'Sábado'];

@Component({
  selector: 'app-panel',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  templateUrl: './panel.html',
  styleUrls: ['./panel.css']
})
export class Panel {
  dias = DIAS;

  // Iniciamos vacío y lo llenamos en el constructor
  sesion = signal<any>(null);
  modoRegistro = signal(false);
  tabActiva = signal<'turnos' | 'servicios' | 'horarios'>('turnos');
  errorAuth = signal<string | null>(null);
  cargandoAuth = signal(false);

  // --- Login / registro ---
  loginEmail = '';
  loginPassword = '';

  regNombre = '';
  regAliasUrl = '';
  regTipoPlantilla = 'Peluquería';
  regTelefono = '';
  regDatosBancarios = '';
  regEmail = '';
  regPassword = '';

  // --- Turnos ---
  turnos = signal<Turno[]>([]);

// --- Servicios ---
  servicios = signal<Servicio[]>([]);
  nuevoServicioNombre = '';
  nuevoServicioDuracion = 30;
  nuevoServicioPrecio = 0;   // Precio total
  nuevoServicioSena = 0;     // Seña

  // --- Horarios ---
  horarios = signal<Horario[]>([]);
  nuevoHorarioDia = 1;
  nuevoHorarioInicio = '10:00';
  nuevoHorarioFin = '19:00';

  constructor(private api: Api, private session: Session) {
    // Leemos la sesión ACÁ adentro, que es seguro
    this.sesion.set(this.session.obtenerUsuario());
    if (this.sesion()) this.cargarTodo();
  }

// ================= AUTH =================
  login(): void {
    this.errorAuth.set(null);
    this.cargandoAuth.set(true);
    this.api.login(this.loginEmail.trim(), this.loginPassword).subscribe({
      next: resp => {
        // Aseguramos que guarde y actualice la señal correctamente
        const datosSesion = resp;
        this.session.iniciarSesion(datosSesion);
        this.sesion.set(datosSesion);
        this.cargandoAuth.set(false);
        this.cargarTodo();
      },
      error: (err) => {
        this.cargandoAuth.set(false);
        console.error('Error en login:', err);
        this.errorAuth.set('Email o contraseña incorrectos.');
      }
    });
  }

  registrar(): void {
    this.errorAuth.set(null);
    this.cargandoAuth.set(true);
    this.api.registrar({
      nombre: this.regNombre.trim(),
      aliasUrl: this.regAliasUrl.trim(),
      tipoPlantilla: this.regTipoPlantilla,
      telefonoNotificaciones: this.regTelefono.trim(),
      datosBancarios: this.regDatosBancarios.trim(),
      email: this.regEmail.trim(),
      password: this.regPassword
    }).subscribe({
      next: resp => {
        this.session.iniciarSesion(resp);
        this.sesion.set(resp);
        this.cargandoAuth.set(false);
        this.cargarTodo();
      },
      error: err => {
        this.cargandoAuth.set(false);
        this.errorAuth.set(err.status === 409 ? err.error?.mensaje ?? 'Ya existe una cuenta con esos datos.' : 'No pudimos crear la cuenta.');
      }
    });
  }

  cerrarSesion(): void {
    this.session.cerrarSesion();
    this.sesion.set(null);
  }

  private cargarTodo(): void {
    this.cargarTurnos();
    this.cargarServicios();
    this.cargarHorarios();
  }

  // ================= TURNOS =================
  cargarTurnos(): void {
    const s = this.sesion();
    if (!s) return;
    this.api.getTurnosDeComercio(s.comercioId).subscribe(turnos => this.turnos.set(turnos));
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

  // ================= SERVICIOS =================
  cargarServicios(): void {
    const s = this.sesion();
    if (!s) return;
    this.api.getServiciosPorComercio(s.comercioId).subscribe(servicios => this.servicios.set(servicios));
  }

  agregarServicio(): void {
    const s = this.sesion();
    if (!s || !this.nuevoServicioNombre.trim()) return;

    this.api.crearServicio({
      comercioId: s.comercioId,
      nombre: this.nuevoServicioNombre.trim(),
      duracionMinutos: this.nuevoServicioDuracion,
      precio: this.nuevoServicioPrecio,     // Enviamos el precio
      montoSeña: this.nuevoServicioSena,  // Enviamos la seña
      activo: true
    }).subscribe(() => {
      this.nuevoServicioNombre = '';
      this.nuevoServicioDuracion = 30;
      this.nuevoServicioSena = 0;
      this.cargarServicios();
    });
  }

  quitarServicio(s: Servicio): void {
    this.api.eliminarServicio(s.id).subscribe(() => this.cargarServicios());
  }

  // ================= HORARIOS =================
  cargarHorarios(): void {
    const s = this.sesion();
    if (!s) return;
    this.api.getHorarios(s.comercioId).subscribe(horarios => this.horarios.set(horarios));
  }

  agregarHorario(): void {
    const s = this.sesion();
    if (!s) return;

    this.api.crearHorario(s.comercioId, {
      diaSemana: this.nuevoHorarioDia,
      horaInicio: this.nuevoHorarioInicio,
      horaFin: this.nuevoHorarioFin
    }).subscribe(() => this.cargarHorarios());
  }

  quitarHorario(h: Horario): void {
    this.api.eliminarHorario(h.id).subscribe(() => this.cargarHorarios());
  }
 
  getPrecio(s: any): number {
    return s.precio || 0;
  }

  getSena(s: any): number {
    return s['montoSeña'] || 0;
  }
}


