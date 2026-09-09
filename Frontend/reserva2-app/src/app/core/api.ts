import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';

// Ajustá esto si corrés el backend en otro puerto (ver Properties/launchSettings.json).
const API_BASE = 'http://localhost:5267/api';

export interface ComercioPublico {
  id: number;
  nombre: string;
  aliasUrl: string;
  tipoPlantilla: string;
  telefonoNotificaciones: string;
}

export interface Servicio {
  id: number;
  comercioId: number;
  nombre: string;
  duracionMinutos: number;
  precio: number;
  montoSeña: number | null;
  activo: boolean;
}

export interface Horario {
  id: number;
  comercioId: number;
  profesionalId: number | null;
  diaSemana: number; // 0 = Domingo ... 6 = Sábado, igual que Date.getDay()
  horaInicio: string; // "HH:mm:ss"
  horaFin: string;
}

export interface Profesional {
  id: number;
  comercioId: number;
  nombre: string;
}

export interface SlotDisponibilidad {
  inicio: string; // ISO
  fin: string;
  disponible: boolean;
}

export interface Turno {
  id: number;
  comercioId: number;
  servicioId: number | null;
  fechaHoraInicio: string;
  fechaHoraFin: string;
  estadoReserva: number; // 1 Pre-Reservado, 2 Confirmado, 3 Cancelado
  clienteNombre: string;
  clienteWhatsApp: string;
  fechaCreacion: string;
}

export interface LoginResponse {
  comercioId: number;
  nombre: string;
  aliasUrl: string;
  token: string;
}

export interface RegistroRequest {
  nombre: string;
  aliasUrl: string;
  tipoPlantilla: string;
  telefonoNotificaciones: string;
  datosBancarios: string;
  email: string;
  password: string;
}

export interface SuperAdminSession {
  token: string;
}

export interface ComercioAdmin {
  id: number;
  nombre: string;
  aliasUrl: string;
  tipoPlantilla: string;
  telefonoNotificaciones: string;
  datosBancarios: string;
  email: string;
  activo: boolean;
  planActual: string;
  fechaProximoPago: string | null;
}

export interface Metricas {
  totalComercios: number;
  comerciosActivos: number;
  comerciosInactivos: number;
  turnosDelMes: number;
}

@Injectable({ providedIn: 'root' })
export class Api {
  constructor(private http: HttpClient) {}

  // --- Auth ---
  login(email: string, password: string): Observable<LoginResponse> {
    return this.http.post<LoginResponse>(`${API_BASE}/auth/login`, { email, password });
  }

  registrar(datos: RegistroRequest): Observable<LoginResponse> {
    return this.http.post<LoginResponse>(`${API_BASE}/auth/register`, datos);
  }

  superAdminLogin(email: string, password: string): Observable<SuperAdminSession> {
    return this.http.post<SuperAdminSession>(`${API_BASE}/auth/super-admin/login`, { email, password });
  }

  // --- Super Admin ---
  getComerciosAdmin(): Observable<ComercioAdmin[]> {
    return this.http.get<ComercioAdmin[]>(`${API_BASE}/comercios`);
  }

  actualizarEstadoComercio(id: number, activo: boolean): Observable<ComercioAdmin> {
    return this.http.patch<ComercioAdmin>(`${API_BASE}/comercios/${id}/estado`, { activo });
  }

  actualizarPlanComercio(id: number, planActual: string): Observable<ComercioAdmin> {
    return this.http.patch<ComercioAdmin>(`${API_BASE}/comercios/${id}/plan`, { planActual });
  }

  getMetricas(): Observable<Metricas> {
    return this.http.get<Metricas>(`${API_BASE}/admin/metricas`);
  }

  // --- Comercios ---
  getComercioPorAlias(alias: string): Observable<ComercioPublico> {
    return this.http.get<ComercioPublico>(`${API_BASE}/comercios/alias/${alias}`);
  }

  // --- Servicios ---
  getServiciosPorComercio(comercioId: number): Observable<Servicio[]> {
    return this.http.get<Servicio[]>(`${API_BASE}/servicios`, { params: { comercioId } });
  }

  crearServicio(servicio: Partial<Servicio>): Observable<Servicio> {
    return this.http.post<Servicio>(`${API_BASE}/servicios`, servicio);
  }

  eliminarServicio(id: number): Observable<void> {
    return this.http.delete<void>(`${API_BASE}/servicios/${id}`);
  }

  // --- Horarios ---
  getHorarios(comercioId: number, profesionalId?: number): Observable<Horario[]> {
    return this.http.get<Horario[]>(`${API_BASE}/comercios/${comercioId}/horarios`, {
      params: profesionalId ? { profesionalId } : {}
    });
  }

  crearHorario(comercioId: number, horario: { diaSemana: number; horaInicio: string; horaFin: string; profesionalId?: number }): Observable<Horario> {
    return this.http.post<Horario>(`${API_BASE}/comercios/${comercioId}/horarios`, horario);
  }

  eliminarHorario(id: number): Observable<void> {
    return this.http.delete<void>(`${API_BASE}/horarios/${id}`);
  }

  // --- Profesionales ---
  getProfesionales(comercioId: number): Observable<Profesional[]> {
    return this.http.get<Profesional[]>(`${API_BASE}/comercios/${comercioId}/profesionales`);
  }

  crearProfesional(comercioId: number, nombre: string): Observable<Profesional> {
    return this.http.post<Profesional>(`${API_BASE}/comercios/${comercioId}/profesionales`, { nombre });
  }

  eliminarProfesional(id: number): Observable<void> {
    return this.http.delete<void>(`${API_BASE}/profesionales/${id}`);
  }

  // --- Disponibilidad y turnos ---
  getDisponibilidad(comercioId: number, servicioId: number, fecha: string): Observable<SlotDisponibilidad[]> {
    return this.http.get<SlotDisponibilidad[]>(`${API_BASE}/comercios/${comercioId}/disponibilidad`, {
      params: { servicioId, fecha }
    });
  }

  crearTurno(turno: {
    comercioId: number;
    servicioId: number;
    fechaHoraInicio: string;
    clienteNombre: string;
    clienteWhatsApp: string;
    clienteEmail: string;
  }): Observable<Turno> {
    return this.http.post<Turno>(`${API_BASE}/turnos`, turno);
  }

  getTurnosDeComercio(comercioId: number, incluirVencidos = false): Observable<Turno[]> {
    return this.http.get<Turno[]>(`${API_BASE}/comercios/${comercioId}/turnos`, {
      params: { incluirVencidos }
    });
  }

  confirmarTurno(id: number): Observable<Turno> {
    return this.http.patch<Turno>(`${API_BASE}/turnos/${id}/confirmar`, {});
  }

  cancelarTurno(id: number): Observable<Turno> {
    return this.http.patch<Turno>(`${API_BASE}/turnos/${id}/cancelar`, {});
  }
}