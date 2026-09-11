import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';

// Ajustá esto si corrés el backend en otro puerto (ver Properties/launchSettings.json).
const API_BASE = 'http://localhost:5267/api';
const API_ROOT = API_BASE.replace(/\/api$/, '');

// Las rutas de archivos (ej. LogoUrl) vienen relativas al backend ("/uploads/logos/1.png"),
// no al frontend, así que hay que anteponerles el host del backend para poder mostrarlas.
export function urlArchivo(ruta: string | null): string | null {
  return ruta ? `${API_ROOT}${ruta}` : null;
}

export interface ComercioPublico {
  id: number;
  nombre: string;
  aliasUrl: string;
  tipoPlantilla: string;
  telefonoNotificaciones: string;
  logoUrl: string | null;
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
  montoCobrado: number | null;
}

export interface HistorialItem {
  id: number;
  fechaHoraInicio: string;
  clienteNombre: string;
  servicioNombre: string;
  monto: number;
}

export interface Historial {
  items: HistorialItem[];
  totalHoy: number;
  totalSemana: number;
  totalMes: number;
}

export interface GananciaPorProfesional {
  profesionalId: number | null;
  nombreProfesional: string;
  cantidadTurnos: number;
  ingresos: number;
}

export interface Ganancias {
  porProfesional: GananciaPorProfesional[];
  cantidadTotal: number;
  ingresosTotal: number;
}

export interface WhatsAppConfig {
  activado: boolean;
}

export interface TurnoPorToken {
  id: number;
  nombreComercio: string;
  nombreServicio: string;
  fechaHoraInicio: string;
  estadoReserva: number;
}

export interface LoginResponse {
  comercioId: number;
  nombre: string;
  aliasUrl: string;
  token: string;
  planActual: string;
  cicloFacturacion: string;
  telefonoNotificaciones: string;
  datosBancarios: string;
  logoUrl: string | null;
}

export interface MiPlan {
  planActual: string;
  cicloFacturacion: string;
}

export interface Perfil {
  nombre: string;
  telefonoNotificaciones: string;
  datosBancarios: string;
  logoUrl: string | null;
}

export interface RegistroRequest {
  nombre: string;
  aliasUrl: string;
  tipoPlantilla: string;
  telefonoNotificaciones: string;
  datosBancarios: string;
  email: string;
  password: string;
  planActual?: string;
  cicloFacturacion?: string;
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
  montoMensualAcordado: number | null;
  cicloFacturacion: string;
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

  actualizarMiPlan(comercioId: number, planActual: string, cicloFacturacion: string): Observable<MiPlan> {
    return this.http.patch<MiPlan>(`${API_BASE}/comercios/${comercioId}/mi-plan`, { planActual, cicloFacturacion });
  }

  subirLogo(comercioId: number, archivo: File): Observable<{ logoUrl: string }> {
    const formData = new FormData();
    formData.append('archivo', archivo);
    return this.http.post<{ logoUrl: string }>(`${API_BASE}/comercios/${comercioId}/logo`, formData);
  }

  actualizarPerfil(comercioId: number, datos: { nombre: string; telefonoNotificaciones: string; datosBancarios: string }): Observable<Perfil> {
    return this.http.patch<Perfil>(`${API_BASE}/comercios/${comercioId}/perfil`, datos);
  }

  cambiarPassword(comercioId: number, passwordActual: string, passwordNueva: string): Observable<{ mensaje: string }> {
    return this.http.post<{ mensaje: string }>(`${API_BASE}/comercios/${comercioId}/cambiar-password`, { passwordActual, passwordNueva });
  }

  olvidoPassword(email: string): Observable<{ mensaje: string }> {
    return this.http.post<{ mensaje: string }>(`${API_BASE}/auth/forgot-password`, { email });
  }

  restablecerPassword(token: string, nuevaPassword: string): Observable<{ mensaje: string }> {
    return this.http.post<{ mensaje: string }>(`${API_BASE}/auth/reset-password`, { token, nuevaPassword });
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

  actualizarMontoAcordado(id: number, montoMensualAcordado: number | null): Observable<ComercioAdmin> {
    return this.http.patch<ComercioAdmin>(`${API_BASE}/comercios/${id}/monto-acordado`, { montoMensualAcordado });
  }

  actualizarCicloFacturacion(id: number, cicloFacturacion: string): Observable<ComercioAdmin> {
    return this.http.patch<ComercioAdmin>(`${API_BASE}/comercios/${id}/ciclo-facturacion`, { cicloFacturacion });
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

  editarServicio(id: number, datos: { nombre: string; duracionMinutos: number; precio: number; montoSeña: number | null }): Observable<Servicio> {
    return this.http.put<Servicio>(`${API_BASE}/servicios/${id}`, datos);
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

  editarHorario(id: number, horario: { diaSemana: number; horaInicio: string; horaFin: string; profesionalId?: number }): Observable<Horario> {
    return this.http.put<Horario>(`${API_BASE}/horarios/${id}`, horario);
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

  editarProfesional(id: number, nombre: string): Observable<Profesional> {
    return this.http.put<Profesional>(`${API_BASE}/profesionales/${id}`, { nombre });
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

  getTurnoPorToken(token: string): Observable<TurnoPorToken> {
    return this.http.get<TurnoPorToken>(`${API_BASE}/turnos/por-token/${token}`);
  }

  cancelarTurnoPorToken(token: string): Observable<{ mensaje: string }> {
    return this.http.post<{ mensaje: string }>(`${API_BASE}/turnos/por-token/${token}/cancelar`, {});
  }

  crearTurnoManual(comercioId: number, turno: {
    servicioId: number;
    fechaHoraInicio: string;
    clienteNombre: string;
    clienteWhatsApp?: string;
    clienteEmail?: string;
    profesionalId?: number;
  }): Observable<Turno> {
    return this.http.post<Turno>(`${API_BASE}/comercios/${comercioId}/turnos`, turno);
  }

  // --- Historial (cortes realizados + control de ingresos) ---
  getHistorial(comercioId: number, anio?: number, mes?: number): Observable<Historial> {
    const params: Record<string, number> = {};
    if (anio) params['anio'] = anio;
    if (mes) params['mes'] = mes;
    return this.http.get<Historial>(`${API_BASE}/comercios/${comercioId}/historial`, { params });
  }

  // --- Ganancias (exclusivo Premium) ---
  getGanancias(comercioId: number, desde: string, hasta: string): Observable<Ganancias> {
    return this.http.get<Ganancias>(`${API_BASE}/comercios/${comercioId}/ganancias`, { params: { desde, hasta } });
  }

  // --- WhatsApp (placeholder, exclusivo Premium) ---
  getWhatsAppConfig(comercioId: number): Observable<WhatsAppConfig> {
    return this.http.get<WhatsAppConfig>(`${API_BASE}/comercios/${comercioId}/whatsapp-config`);
  }

  actualizarWhatsAppConfig(comercioId: number, activado: boolean): Observable<WhatsAppConfig> {
    return this.http.post<WhatsAppConfig>(`${API_BASE}/comercios/${comercioId}/whatsapp-config`, { activado });
  }
}