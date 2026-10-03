import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';

// Usa el mismo host desde el que se sirvió la página (localhost o la IP de la red local),
// así funciona igual accediendo desde la compu o desde el celular en el mismo WiFi.
// Ajustá el puerto si corrés el backend en otro (ver Properties/launchSettings.json).
const esLocal = typeof window !== 'undefined' && window.location.hostname === 'localhost';
const API_ROOT = esLocal ? 'http://localhost:5267' : '';
const API_BASE = `${API_ROOT}/api`;

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
  whatsAppActivo: boolean;
  datosBancarios: string; // alias/CBU para transferir la seña
  // Formas de pagar la seña que ofrece el comercio (al menos una siempre es true).
  señaMercadoPago: boolean;
  señaTransferencia: boolean;
  // true = por Mercado Pago se cobra el servicio completo (no la seña).
  cobroMercadoPagoTotal: boolean;
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
  sucursalId: number | null;
  especialidad: string | null; // opcional, máx. 30 caracteres (ej.: "Barbero")
  fotoUrl: string | null; // relativa al backend (usar urlArchivo); null = mostrar iniciales
}

export interface Sucursal {
  id: number;
  comercioId: number;
  nombre: string;
  direccion: string;
  telefono: string | null;
  activa: boolean;
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
  profesionalId: number | null;
  fechaHoraInicio: string;
  fechaHoraFin: string;
  estadoReserva: number; // 1 Pre-Reservado, 2 Confirmado, 3 Cancelado
  clienteNombre: string;
  clienteWhatsApp: string;
  fechaCreacion: string;
  montoCobrado: number | null;
  // Seña: null = el servicio no pedía seña; false = a verificar; true = verificada.
  señaVerificada: boolean | null;
  // Cómo se paga la seña: 'Transferencia' (comprobante) o 'MercadoPago'. Un turno con seña
  // por Mercado Pago queda en pre-reserva (estado 1) hasta que se aprueba el pago.
  señaMedio: 'Transferencia' | 'MercadoPago' | null;
  tieneComprobante: boolean;
  // De dónde vino la reserva. Null en los turnos anteriores a que se guardara el dato.
  origen: 'PaginaPublica' | 'Panel' | null;
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

export interface GananciaPorSucursal {
  sucursalId: number | null;
  nombreSucursal: string;
  cantidadTurnos: number;
  ingresos: number;
}

export interface FacturacionDia {
  fecha: string;
  total: number;
}

export interface ServicioPedido {
  servicioId: number | null;
  nombreServicio: string;
  cantidad: number;
}

export interface HorarioOcupado {
  hora: number;
  cantidad: number;
}

export interface ClienteFrecuente {
  nombre: string;
  cantidadTurnos: number;
  ultimaVisita: string;
}

export interface ClienteReactivar {
  nombre: string;
  cantidadTurnos: number;
  ultimaVisita: string;
}

// Resumen de la pestaña Inicio (GET /comercios/{id}/resumen). "Turnos" son los que ocupan
// horario: confirmados o pre-reservas vigentes, igual que en la grilla pública.
export interface Resumen {
  desde: string;
  hasta: string;
  turnos: number;
  turnosPeriodoAnterior: number;
  ingresosEstimados: number;
  ingresosPeriodoAnterior: number;
  minutosReservados: number;
  minutosDisponibles: number;
  turnosPorDia: { fecha: string; cantidad: number }[];
  horariosMasPedidos: { diaSemana: number; hora: number; cantidad: number }[];
  serviciosMasReservados: { servicioId: number | null; nombreServicio: string; cantidad: number; ingresos: number }[];
  reservasPorHoraDeCreacion: { hora: number; cantidad: number }[];
  reservasOnline: number;
  reservasFueraDeHorario: number;
  ocupacionPorProfesional: { profesionalId: number; nombre: string; minutosReservados: number; minutosDisponibles: number }[];
}

export interface Ganancias {
  porProfesional: GananciaPorProfesional[];
  porSucursal: GananciaPorSucursal[];
  cantidadTotal: number;
  ingresosTotal: number;
  facturadoHoy: number;
  facturadoUltimos7Dias: number;
  facturadoEsteMes: number;
  cortesTotales: number;
  facturacionPorDia: FacturacionDia[];
  servicioMasPedido: ServicioPedido[];
  horariosOcupados: HorarioOcupado[];
  clientesFrecuentes: ClienteFrecuente[];
  paraReactivar: ClienteReactivar[];
}

export interface WhatsAppConfig {
  activado: boolean;
}

// Cuenta de Mercado Pago del comercio para cobrar señas. "disponible" = Reserva2 ya tiene
// configurada su aplicación de Mercado Pago (si no, no se puede conectar todavía).
export interface MercadoPagoConfig {
  disponible: boolean;
  conectado: boolean;
  cuentaId: number | null;
  señaPorMercadoPago: boolean;
  señaPorTransferencia: boolean;
  // Qué se cobra por Mercado Pago al reservar: true = el servicio completo, false = la seña.
  cobroTotal: boolean;
  // Extra "Cobros automáticos" (se paga aparte del plan). Sin él no se puede cobrar por Mercado Pago.
  addonActivo: boolean;
  precioAddon: number;
}

// "confirmado" | "pendiente" | "rechazado" | "horario_ocupado" | "sin_pago" | "cancelado" | "desconocido"
export interface EstadoPagoSenia {
  estado: string;
}

export interface EstadoPagoPlan {
  estado: 'Pendiente' | 'Aprobado' | 'Rechazado';
  planActual: string;
  cicloFacturacion: string;
  fechaProximoPago: string | null;
  activo: boolean;
}

export interface PagoPlan {
  id: number;
  fecha: string;
  monto: number;
  plan: string;
  ciclo: string;
  medio: 'MercadoPago' | 'Transferencia';
  estado: string;
  mercadoPagoPagoId: number | null;
}

export interface TurnoPorToken {
  id: number;
  nombreComercio: string;
  nombreServicio: string;
  fechaHoraInicio: string;
  estadoReserva: number;
  // false si el turno ya pasó o faltan menos de horasMinimasParaCancelar para que empiece.
  puedeCancelar: boolean;
  horasMinimasParaCancelar: number;
  telefonoComercio: string | null;
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
  fechaProximoPago: string | null;
  activo: boolean;
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
  cantidadProfesionales?: number;
  cantidadSucursales?: number;
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

export interface TurnosPorSemana {
  desde: string;
  hasta: string;
  cantidad: number;
}

export interface ComercioDetalle {
  id: number;
  addonCobrosOnline: boolean;      // extra "Cobros automáticos con Mercado Pago"
  mercadoPagoConectado: boolean;
  nombre: string;
  aliasUrl: string;
  tipoPlantilla: string;
  planActual: string;
  cicloFacturacion: string;
  montoMensualAcordado: number | null;
  fechaAlta: string;
  fechaProximoPago: string | null;
  activo: boolean;
  cantidadProfesionales: number;
  cantidadSucursales: number;
  turnosHistoricosTotal: number;
  turnosDelMes: number;
  facturacionHistorica: number | null;
  sucursales: Sucursal[];
  profesionales: Profesional[];
  turnosPorSemana: TurnosPorSemana[];
  // Para el panel lateral del Super Admin.
  email: string;
  telefonoNotificaciones: string;
  whatsAppNumero: string | null; // ya formateado para wa.me (549...)
  ultimoAcceso: string | null;   // UTC
  fechaActivacion: string | null;
  fechaBaja: string | null;
  cantidadServicios: number;
  facturacionDelMes: number;
  turnosPorDia: { fecha: string; cantidad: number }[];
  topeTurnosGratuito: number;
}

export interface Metricas {
  totalComercios: number;
  comerciosActivos: number;
  comerciosInactivos: number;
  turnosDelMes: number;
}

export interface PlanCantidad {
  plan: string;
  cantidad: number;
}

export interface AltaMes {
  anio: number;
  mes: number;
  cantidad: number;
}

// Datos extra por comercio para la tabla del Super Admin (GET /admin/comercios/estadisticas).
export interface ComercioEstadistica {
  id: number;
  fechaAlta: string;
  pendiente: boolean;
  turnosDelMes: number;
  turnosMesAnterior: number;
  // Último login del dueño al panel (UTC); null si no entró desde que se registra.
  ultimoAcceso: string | null;
}

// Resumen del Super Admin (GET /admin/resumen).
export interface SuperAdminResumen {
  mrrActual: number;
  mrrMesAnterior: number | null;
  mrrHistorico: { anio: number; mes: number; monto: number }[];
  turnosDelMes: number;
  turnosMesAnterior: number;
  facturacionLocalesMes: number;
  facturacionLocalesMesAnterior: number;
  turnosPorDia: { fecha: string; cantidad: number }[];
  altasYBajasPorMes: { anio: number; mes: number; altas: number; bajas: number }[];
  porRubro: { rubro: string; cantidad: number }[];
}

export interface Dashboard {
  totalComercios: number;
  comerciosActivos: number;
  comerciosInactivos: number;
  comerciosPendientes: number;
  porPlan: PlanCantidad[];
  ingresoMensualEstimado: number;
  turnosDelMes: number;
  altasPorMes: AltaMes[];
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

  getPrecioPlan(plan: string, ciclo: string, profesionales: number, sucursales: number): Observable<{ precio: number }> {
    return this.http.get<{ precio: number }>(`${API_BASE}/precio-plan`, {
      params: { plan, ciclo, profesionales, sucursales }
    });
  }

  superAdminLogin(email: string, password: string): Observable<SuperAdminSession> {
    return this.http.post<SuperAdminSession>(`${API_BASE}/auth/super-admin/login`, { email, password });
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

  getEstadisticasComercios(): Observable<ComercioEstadistica[]> {
    return this.http.get<ComercioEstadistica[]>(`${API_BASE}/admin/comercios/estadisticas`);
  }

  getResumenSuperAdmin(): Observable<SuperAdminResumen> {
    return this.http.get<SuperAdminResumen>(`${API_BASE}/admin/resumen`);
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

  actualizarAddonCobros(id: number, activo: boolean): Observable<{ addonCobrosOnline: boolean }> {
    return this.http.patch<{ addonCobrosOnline: boolean }>(`${API_BASE}/comercios/${id}/addon-cobros`, { activo });
  }

  renovarComercio(id: number): Observable<ComercioAdmin> {
    return this.http.patch<ComercioAdmin>(`${API_BASE}/comercios/${id}/renovar`, {});
  }

  eliminarComercio(id: number): Observable<void> {
    return this.http.delete<void>(`${API_BASE}/comercios/${id}`);
  }

  getMetricas(): Observable<Metricas> {
    return this.http.get<Metricas>(`${API_BASE}/admin/metricas`);
  }

  getDashboard(): Observable<Dashboard> {
    return this.http.get<Dashboard>(`${API_BASE}/admin/dashboard`);
  }

  getComercioDetalle(id: number): Observable<ComercioDetalle> {
    return this.http.get<ComercioDetalle>(`${API_BASE}/admin/comercios/${id}/detalle`);
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
  getProfesionales(comercioId: number, sucursalId?: number): Observable<Profesional[]> {
    return this.http.get<Profesional[]>(`${API_BASE}/comercios/${comercioId}/profesionales`, {
      params: sucursalId ? { sucursalId } : {}
    });
  }

  crearProfesional(comercioId: number, nombre: string, sucursalId?: number | null, especialidad?: string | null): Observable<Profesional> {
    return this.http.post<Profesional>(`${API_BASE}/comercios/${comercioId}/profesionales`, { nombre, sucursalId, especialidad });
  }

  editarProfesional(id: number, nombre: string, sucursalId?: number | null, especialidad?: string | null): Observable<Profesional> {
    return this.http.put<Profesional>(`${API_BASE}/profesionales/${id}`, { nombre, sucursalId, especialidad });
  }

  eliminarProfesional(id: number): Observable<void> {
    return this.http.delete<void>(`${API_BASE}/profesionales/${id}`);
  }

  subirFotoProfesional(id: number, archivo: File): Observable<{ fotoUrl: string }> {
    const formData = new FormData();
    formData.append('archivo', archivo);
    return this.http.post<{ fotoUrl: string }>(`${API_BASE}/profesionales/${id}/foto`, formData);
  }

  quitarFotoProfesional(id: number): Observable<void> {
    return this.http.delete<void>(`${API_BASE}/profesionales/${id}/foto`);
  }

  // --- Sucursales ---
  getSucursales(comercioId: number): Observable<Sucursal[]> {
    return this.http.get<Sucursal[]>(`${API_BASE}/comercios/${comercioId}/sucursales`);
  }

  crearSucursal(comercioId: number, datos: { nombre: string; direccion: string; telefono?: string | null; activa?: boolean }): Observable<Sucursal> {
    return this.http.post<Sucursal>(`${API_BASE}/comercios/${comercioId}/sucursales`, datos);
  }

  editarSucursal(id: number, datos: { nombre: string; direccion: string; telefono?: string | null; activa?: boolean }): Observable<Sucursal> {
    return this.http.put<Sucursal>(`${API_BASE}/sucursales/${id}`, datos);
  }

  eliminarSucursal(id: number): Observable<void> {
    return this.http.delete<void>(`${API_BASE}/sucursales/${id}`);
  }

  // --- Disponibilidad y turnos ---
  getDisponibilidad(comercioId: number, servicioId: number, fecha: string, profesionalId?: number | null, sucursalId?: number | null): Observable<SlotDisponibilidad[]> {
    const params: Record<string, string | number> = { servicioId, fecha };
    if (profesionalId) params['profesionalId'] = profesionalId;
    if (sucursalId) params['sucursalId'] = sucursalId;
    return this.http.get<SlotDisponibilidad[]>(`${API_BASE}/comercios/${comercioId}/disponibilidad`, { params });
  }

  crearTurno(turno: {
    comercioId: number;
    servicioId: number;
    fechaHoraInicio: string;
    clienteNombre: string;
    clienteWhatsApp: string;
    clienteEmail: string;
    profesionalId?: number | null;
    comprobanteBase64?: string | null; // captura del comprobante, solo si el servicio pide seña
    medioSenia?: 'MercadoPago' | 'Transferencia' | null;
  }): Observable<{ id: number; urlPago?: string }> {
    // Con seña por Mercado Pago la respuesta trae urlPago: el turno queda apartado hasta que
    // el cliente pague ahí.
    return this.http.post<{ id: number; urlPago?: string }>(`${API_BASE}/turnos`, turno);
  }

  verificarPagoSenia(tokenTurno: string, pagoId: number | null): Observable<EstadoPagoSenia> {
    return this.http.post<EstadoPagoSenia>(`${API_BASE}/mercadopago/senias/${encodeURIComponent(tokenTurno)}/verificar`, { pagoId });
  }

  abandonarPagoSenia(tokenTurno: string): Observable<void> {
    return this.http.post<void>(`${API_BASE}/mercadopago/senias/${encodeURIComponent(tokenTurno)}/abandonar`, {});
  }

  // incluirPasados: además de los activos, trae los confirmados que ya pasaron (para el calendario).
  getTurnosDeComercio(comercioId: number, incluirVencidos = false, incluirPasados = false): Observable<Turno[]> {
    return this.http.get<Turno[]>(`${API_BASE}/comercios/${comercioId}/turnos`, {
      params: { incluirVencidos, incluirPasados }
    });
  }

  confirmarTurno(id: number): Observable<Turno> {
    return this.http.patch<Turno>(`${API_BASE}/turnos/${id}/confirmar`, {});
  }

  cancelarTurno(id: number): Observable<Turno> {
    return this.http.patch<Turno>(`${API_BASE}/turnos/${id}/cancelar`, {});
  }

  // Captura del comprobante de seña (solo el comercio dueño); viene como imagen.
  getComprobanteTurno(id: number): Observable<Blob> {
    return this.http.get(`${API_BASE}/turnos/${id}/comprobante`, { responseType: 'blob' });
  }

  marcarSeniaVerificada(id: number): Observable<Turno> {
    return this.http.patch<Turno>(`${API_BASE}/turnos/${id}/sena-verificada`, {});
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

  getResumen(comercioId: number, desde: string, hasta: string): Observable<Resumen> {
    return this.http.get<Resumen>(`${API_BASE}/comercios/${comercioId}/resumen`, { params: { desde, hasta } });
  }

  // --- WhatsApp (placeholder, exclusivo Premium) ---
  getWhatsAppConfig(comercioId: number): Observable<WhatsAppConfig> {
    return this.http.get<WhatsAppConfig>(`${API_BASE}/comercios/${comercioId}/whatsapp-config`);
  }

  actualizarWhatsAppConfig(comercioId: number, activado: boolean): Observable<WhatsAppConfig> {
    return this.http.post<WhatsAppConfig>(`${API_BASE}/comercios/${comercioId}/whatsapp-config`, { activado });
  }

  // --- Mercado Pago: cuenta del comercio (señas) ---
  getMercadoPagoConfig(comercioId: number): Observable<MercadoPagoConfig> {
    return this.http.get<MercadoPagoConfig>(`${API_BASE}/comercios/${comercioId}/mercadopago`);
  }

  conectarMercadoPago(comercioId: number): Observable<{ url: string }> {
    return this.http.post<{ url: string }>(`${API_BASE}/comercios/${comercioId}/mercadopago/conectar`, {});
  }

  actualizarMercadoPagoConfig(comercioId: number, señaPorMercadoPago: boolean, señaPorTransferencia: boolean, cobroTotal: boolean): Observable<MercadoPagoConfig> {
    return this.http.patch<MercadoPagoConfig>(`${API_BASE}/comercios/${comercioId}/mercadopago`, { señaPorMercadoPago, señaPorTransferencia, cobroTotal });
  }

  desconectarMercadoPago(comercioId: number): Observable<MercadoPagoConfig> {
    return this.http.delete<MercadoPagoConfig>(`${API_BASE}/comercios/${comercioId}/mercadopago`);
  }

  // --- Mercado Pago: pago del plan ---
  getMontoPagoPlan(comercioId: number, plan: string, ciclo: string): Observable<{ monto: number; disponible: boolean }> {
    return this.http.get<{ monto: number; disponible: boolean }>(`${API_BASE}/comercios/${comercioId}/pago-plan`, { params: { plan, ciclo } });
  }

  pagarPlan(comercioId: number, plan: string, ciclo: string): Observable<{ urlPago: string; monto: number }> {
    return this.http.post<{ urlPago: string; monto: number }>(`${API_BASE}/comercios/${comercioId}/pago-plan`, { plan, ciclo });
  }

  verificarPagoPlan(comercioId: number, pagoId: number, pagoMercadoPagoId: number | null): Observable<EstadoPagoPlan> {
    return this.http.post<EstadoPagoPlan>(`${API_BASE}/comercios/${comercioId}/pago-plan/${pagoId}/verificar`, { pagoId: pagoMercadoPagoId });
  }

  getPagosComercio(comercioId: number): Observable<PagoPlan[]> {
    return this.http.get<PagoPlan[]>(`${API_BASE}/admin/comercios/${comercioId}/pagos`);
  }
}