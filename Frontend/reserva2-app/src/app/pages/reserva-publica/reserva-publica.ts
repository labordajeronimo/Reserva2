import { Component, OnInit, signal, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { catchError, forkJoin, of } from 'rxjs';

import { Api, ComercioPublico, Servicio, SlotDisponibilidad, Sucursal, Profesional, urlArchivo } from '../../core/api';

// Foto de la reserva recién hecha para la pantalla de confirmación (el formulario se puede
// resetear con "Reservar otro turno" sin perder lo que se muestra).
interface ReservaHecha {
  primerNombre: string;
  email: string;
  diaLargo: string;
  horario: string;
  servicio: string;
  profesional: string | null;
  local: string | null;
  total: number;
  conSenia: boolean;
  seniaMercadoPago: boolean;
  pagoCompleto: boolean; // por Mercado Pago se pagó el servicio entero, no solo la seña
  linkCalendario: string;
}

// La reserva con seña por Mercado Pago se termina en el checkout de Mercado Pago (otra
// página); lo que hace falta para mostrar la confirmación a la vuelta se guarda acá.
const CLAVE_RESERVA_PENDIENTE_MP = 'reserva2_reserva_pendiente_mp';

interface DiaGrilla {
  fecha: string; // yyyy-MM-dd, lo que le mandamos a la API
  etiquetaDia: string; // "mar", "mié"...
  numeroDia: number;
  etiquetaCompleta: string; // "Miércoles 10"
}

const DIAS_CORTOS = ['dom', 'lun', 'mar', 'mié', 'jue', 'vie', 'sáb'];
const DIAS_LARGOS = ['Domingo', 'Lunes', 'Martes', 'Miércoles', 'Jueves', 'Viernes', 'Sábado'];

const EMAIL_REGEX = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;
const WHATSAPP_REGEX = /^[0-9+()\-\s]{8,20}$/;

@Component({
  selector: 'app-reserva-publica',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  templateUrl: './reserva-publica.html',
  styleUrls: ['./reserva-publica.css']
})
export class ReservaPublica implements OnInit {
  comercio = signal<ComercioPublico | null>(null);
  cargandoComercio = signal(true);
  comercioInactivo = signal(false);

  // Sucursales: si el comercio tiene más de una activa, el cliente tiene que elegir una
  // antes de ver servicios/horarios. Si tiene 0 o 1, el flujo sigue igual que siempre.
  sucursales = signal<Sucursal[]>([]);
  sucursalesActivas = computed(() => this.sucursales().filter(s => s.activa));
  necesitaElegirSucursal = computed(() => this.sucursalesActivas().length > 1);
  sucursalSeleccionada = signal<Sucursal | null>(null);

  // Profesionales: el cliente siempre puede elegir uno (o "cualquiera disponible") si el
  // comercio cargó alguno; si no cargó ninguno, no se muestra el paso, igual que siempre.
  profesionales = signal<Profesional[]>([]);
  profesionalSeleccionado = signal<Profesional | null>(null);

  servicios = signal<Servicio[]>([]);
  servicioSeleccionado = signal<Servicio | null>(null);

  diasDisponibles: DiaGrilla[] = this.generarProximosDias(7);
  diaSeleccionado = signal<DiaGrilla | null>(null);
  fechaManual = '';
  mostrarFechaManual = signal(false);

  // Cuántos horarios hay (total) y cuántos libres en cada uno de los 7 días, para apagar los
  // días cerrados y mostrar los libres. Sale del mismo endpoint público de disponibilidad,
  // una llamada por día; un día sin ningún horario es un día en que el local no atiende.
  resumenDias = signal<Record<string, { total: number; libres: number }>>({});
  private consultaResumenDias = 0;
  fechaMinima = this.formatearFechaISO(new Date());

  slots = signal<SlotDisponibilidad[]>([]);
  slotSeleccionado = signal<SlotDisponibilidad | null>(null);
  cargandoSlots = signal(false);

  clienteNombre = '';
  clienteWhatsApp = '';
  clienteEmail = '';
  reservando = signal(false);
  errorReserva = signal<string | null>(null);
  reservaConfirmada = signal(false);
  reservaHecha = signal<ReservaHecha | null>(null);
  linkCopiado = signal(false);
  intentoEnviar = signal(false);
  // Los errores de cada campo se muestran recién cuando el cliente sale del campo (o intentó
  // reservar), no mientras está escribiendo.
  camposTocados = signal<Set<string>>(new Set());

  // Seña: si el servicio la pide, el comprobante (captura) es obligatorio para reservar.
  comprobanteBase64 = signal<string | null>(null);
  comprobanteVistaPrevia = signal<string | null>(null);
  comprobanteNombre = signal('');
  errorComprobante = signal<string | null>(null);
  aliasCopiado = signal(false);

  // Cómo paga la seña el cliente, si el comercio ofrece las dos formas.
  medioSenia = signal<'MercadoPago' | 'Transferencia'>('Transferencia');
  // Resultado del pago de la seña al volver de Mercado Pago, cuando no hay pantalla de
  // confirmación para mostrar (pago pendiente, rechazado, etc.).
  avisoPagoMp = signal<{ tipo: 'ok' | 'info' | 'error'; texto: string } | null>(null);
  verificandoPagoMp = signal(false);

  hostActual = typeof window !== 'undefined' ? window.location.host : 'reservados2.com';

  logoUrl = computed(() => urlArchivo(this.comercio()?.logoUrl ?? null));

  iniciales = computed(() => {
    const nombre = this.comercio()?.nombre ?? '';
    return nombre
      .split(' ')
      .filter(p => p.length > 0)
      .slice(0, 2)
      .map(p => p[0]!.toUpperCase())
      .join('');
  });

  marcarTocado(campo: string): void {
    this.camposTocados.update(s => new Set(s).add(campo));
  }

  private mostrarError(campo: string): boolean {
    return this.intentoEnviar() || this.camposTocados().has(campo);
  }

  errorNombre(): string | null {
    if (!this.mostrarError('nombre')) return null;
    return this.clienteNombre.trim().length > 1 ? null : 'Ingresá tu nombre.';
  }

  errorWhatsApp(): string | null {
    if (!this.mostrarError('whatsapp')) return null;
    const valor = this.clienteWhatsApp.trim();
    if (valor.length === 0) return 'Ingresá tu WhatsApp.';
    const cantidadDigitos = (valor.match(/\d/g) ?? []).length;
    if (!WHATSAPP_REGEX.test(valor) || cantidadDigitos < 8) {
      return 'Ingresá un WhatsApp válido, con código de área (ej: 341 000 0000).';
    }
    return null;
  }

  errorEmail(): string | null {
    if (!this.mostrarError('email')) return null;
    const valor = this.clienteEmail.trim();
    if (valor.length === 0) return 'Ingresá tu email.';
    if (!EMAIL_REGEX.test(valor)) return 'Ingresá un email válido (ej: tu@email.com).';
    return null;
  }

  formularioValido(): boolean {
    return this.clienteNombre.trim().length > 1
      && WHATSAPP_REGEX.test(this.clienteWhatsApp.trim())
      && (this.clienteWhatsApp.trim().match(/\d/g) ?? []).length >= 8
      && EMAIL_REGEX.test(this.clienteEmail.trim());
  }

  private servicioTieneSenia(): boolean {
    return (this.servicioSeleccionado()?.['montoSeña'] ?? 0) > 0;
  }

  // Lo que se cobra por Mercado Pago al reservar (mismo criterio que MontoMercadoPagoServicio
  // en el backend): el precio completo si el local eligió cobrar el servicio entero, si no la
  // seña. Null si el local no cobra por Mercado Pago o el servicio no tiene nada para cobrar.
  montoMercadoPago(): number | null {
    const comercio = this.comercio();
    const servicio = this.servicioSeleccionado();
    if (!comercio?.señaMercadoPago || !servicio) return null;
    if (comercio.cobroMercadoPagoTotal && servicio.precio > 0) return servicio.precio;
    return this.servicioTieneSenia() ? servicio.montoSeña : null;
  }

  pagoCompletoMercadoPago(): boolean {
    const monto = this.montoMercadoPago();
    return monto !== null && monto >= (this.servicioSeleccionado()?.precio ?? 0);
  }

  // Hay que pagar algo al reservar: la seña del servicio, o el turno por Mercado Pago.
  pideSenia(): boolean {
    return this.servicioTieneSenia() || this.montoMercadoPago() !== null;
  }

  tituloPago(): string {
    const servicio = this.servicioSeleccionado();
    if (!servicio) return '';
    const formato = (n: number) => `$${n.toLocaleString('es-AR')}`;
    if (this.seniaPorMercadoPago() && this.pagoCompletoMercadoPago()) return `Este turno se paga al reservar: ${formato(servicio.precio)}`;
    return `Este servicio pide una seña de ${formato(servicio.montoSeña ?? 0)}`;
  }

  // Un servicio sin seña que el local cobra entero por Mercado Pago solo se paga por ahí.
  seniaPorMercadoPago(): boolean {
    return this.montoMercadoPago() !== null && (this.medioSenia() === 'MercadoPago' || !this.servicioTieneSenia());
  }

  ofreceAmbosMedios(): boolean {
    const c = this.comercio();
    return !!c && c.señaMercadoPago && c.señaTransferencia && this.servicioTieneSenia();
  }

  elegirMedioSenia(medio: 'MercadoPago' | 'Transferencia'): void {
    this.medioSenia.set(medio);
    this.errorReserva.set(null);
  }

  puedeReservar(): boolean {
    return !!this.servicioSeleccionado() &&
      !!this.slotSeleccionado() &&
      this.slotSeleccionado()!.disponible &&
      this.formularioValido() &&
      (!this.pideSenia() || this.seniaPorMercadoPago() || !!this.comprobanteBase64());
  }

  // El botón dice lo que falta, en el orden en que se completa el formulario.
  textoBotonReservar(): string {
    if (!this.servicioSeleccionado()) return 'Elegí un servicio';
    if (!this.slotSeleccionado()) return 'Elegí un horario';
    if (this.clienteNombre.trim().length <= 1) return 'Completá tu nombre';
    const whatsapp = this.clienteWhatsApp.trim();
    if (!WHATSAPP_REGEX.test(whatsapp) || (whatsapp.match(/\d/g) ?? []).length < 8) return 'Completá tu WhatsApp';
    if (!EMAIL_REGEX.test(this.clienteEmail.trim())) return 'Completá tu email';
    if (this.seniaPorMercadoPago()) {
      const que = this.pagoCompletoMercadoPago() ? 'turno' : 'seña';
      return `Pagar ${que} con Mercado Pago · $${(this.montoMercadoPago() ?? 0).toLocaleString('es-AR')}`;
    }
    if (this.pideSenia() && !this.comprobanteBase64()) return 'Subí el comprobante de la seña';
    const precio = this.servicioSeleccionado()!.precio;
    return precio > 0 ? `Reservar turno · $${precio.toLocaleString('es-AR')}` : 'Reservar turno';
  }

  // ================= RESUMEN =================
  mesCorto(): string {
    const dia = this.diaSeleccionado();
    if (!dia) return '';
    const [y, m, d] = dia.fecha.split('-').map(Number);
    return new Date(y, m - 1, d).toLocaleDateString('es-AR', { month: 'short' }).replace('.', '');
  }

  diaLargoSeleccionado(): string {
    const dia = this.diaSeleccionado();
    if (!dia) return '';
    const [y, m, d] = dia.fecha.split('-').map(Number);
    const texto = new Date(y, m - 1, d).toLocaleDateString('es-AR', { weekday: 'long', day: 'numeric', month: 'long' });
    return texto.charAt(0).toUpperCase() + texto.slice(1);
  }

  textoHorarioSeleccionado(): string {
    const slot = this.slotSeleccionado();
    return slot ? `${this.formatearHora(slot.inicio)} a ${this.formatearHora(slot.fin)} hs` : '';
  }

  // ================= SEÑA =================
  elegirComprobante(evento: Event): void {
    const input = evento.target as HTMLInputElement;
    const archivo = input.files?.[0];
    input.value = '';
    if (!archivo) return;

    this.errorComprobante.set(null);
    if (!['image/png', 'image/jpeg', 'image/webp'].includes(archivo.type)) {
      this.errorComprobante.set('Subí una imagen PNG, JPG o WEBP (una captura de pantalla).');
      return;
    }
    if (archivo.size > 5 * 1024 * 1024) {
      this.errorComprobante.set('La imagen no puede pesar más de 5MB.');
      return;
    }

    const lector = new FileReader();
    lector.onload = () => {
      const dataUrl = lector.result as string;
      this.comprobanteBase64.set(dataUrl);
      this.comprobanteVistaPrevia.set(dataUrl);
      this.comprobanteNombre.set(archivo.name);
    };
    lector.onerror = () => this.errorComprobante.set('No pudimos leer la imagen. Probá con otra.');
    lector.readAsDataURL(archivo);
  }

  quitarComprobante(): void {
    this.comprobanteBase64.set(null);
    this.comprobanteVistaPrevia.set(null);
    this.comprobanteNombre.set('');
    this.errorComprobante.set(null);
  }

  copiarAlias(): void {
    const alias = this.comercio()?.datosBancarios ?? '';
    navigator.clipboard?.writeText(alias).then(() => {
      this.aliasCopiado.set(true);
      setTimeout(() => this.aliasCopiado.set(false), 1600);
    });
  }

  // ================= CONFIRMACIÓN =================
  // Link de Google Calendar con el mismo formato que el del mail (LinkGoogleCalendar en el
  // backend): los horarios vienen en hora de Argentina sin zona, y Argentina es UTC-3 fijo.
  private linkGoogleCalendar(inicio: string, fin: string, titulo: string, ubicacion: string, detalle: string): string {
    const aUtc = (iso: string) => {
      const [fecha, hora] = iso.split('T');
      const [y, m, d] = fecha.split('-').map(Number);
      const [hh, mm] = hora.split(':').map(Number);
      return new Date(Date.UTC(y, m - 1, d, hh + 3, mm)).toISOString().replace(/[-:]/g, '').replace(/\.\d{3}/, '');
    };
    const params = [
      'action=TEMPLATE',
      `text=${encodeURIComponent(titulo)}`,
      `dates=${aUtc(inicio)}/${aUtc(fin)}`,
      `details=${encodeURIComponent(detalle)}`,
      `location=${encodeURIComponent(ubicacion)}`
    ];
    return `https://calendar.google.com/calendar/render?${params.join('&')}`;
  }

  private armarReservaHecha(): ReservaHecha | null {
    const comercio = this.comercio();
    const servicio = this.servicioSeleccionado();
    const slot = this.slotSeleccionado();
    if (!comercio || !servicio || !slot) return null;

    const sucursal = this.sucursalSeleccionada();
    const profesional = this.profesionalSeleccionado();
    const ubicacion = sucursal?.direccion ? `${comercio.nombre} · ${sucursal.direccion}` : comercio.nombre;
    return {
      primerNombre: this.clienteNombre.trim().split(/\s+/)[0] ?? '',
      email: this.clienteEmail.trim(),
      diaLargo: this.diaLargoSeleccionado(),
      horario: this.textoHorarioSeleccionado(),
      servicio: servicio.nombre,
      profesional: this.profesionales().length > 0 ? (profesional?.nombre ?? 'Cualquiera disponible') : null,
      local: sucursal?.nombre ?? null,
      total: servicio.precio,
      conSenia: this.pideSenia(),
      seniaMercadoPago: this.seniaPorMercadoPago(),
      pagoCompleto: this.seniaPorMercadoPago() && this.pagoCompletoMercadoPago(),
      linkCalendario: this.linkGoogleCalendar(slot.inicio, slot.fin, `${servicio.nombre} en ${comercio.nombre}`, ubicacion,
        `Turno reservado con Reserva2.${profesional ? ' Te atiende ' + profesional.nombre + '.' : ''}`)
    };
  }

  constructor(private route: ActivatedRoute, private router: Router, private api: Api) {}

  ngOnInit(): void {
    const alias = this.route.snapshot.paramMap.get('alias');
    if (!alias) {
      this.cargandoComercio.set(false);
      return;
    }

    this.api.getComercioPorAlias(alias).subscribe({
      next: comercio => {
        this.comercio.set(comercio);
        // Si el local cobra la seña por Mercado Pago, es la opción que aparece elegida.
        this.medioSenia.set(comercio.señaMercadoPago ? 'MercadoPago' : 'Transferencia');
        this.cargandoComercio.set(false);
        this.cargarSucursales(comercio.id);
        this.procesarVueltaDeMercadoPago();
      },
      error: err => {
        if (err.status === 403) this.comercioInactivo.set(true);
        this.cargandoComercio.set(false);
      }
    });
  }

  // ================= VUELTA DE MERCADO PAGO =================
  // Mercado Pago vuelve a /{alias}?pagoTurno={token}&payment_id=...&status=... (payment_id y
  // status los agrega Mercado Pago; si el cliente salió sin pagar pueden venir vacíos o "null").
  private procesarVueltaDeMercadoPago(): void {
    const query = this.route.snapshot.queryParamMap;
    const token = query.get('pagoTurno');
    if (!token) return;

    const pagoIdTexto = query.get('payment_id') ?? query.get('collection_id');
    const pagoId = pagoIdTexto && /^\d+$/.test(pagoIdTexto) ? Number(pagoIdTexto) : null;
    const statusUrl = query.get('status') ?? query.get('collection_status');

    let reservaGuardada: ReservaHecha | null = null;
    try {
      const guardada = sessionStorage.getItem(CLAVE_RESERVA_PENDIENTE_MP);
      reservaGuardada = guardada ? JSON.parse(guardada) : null;
    } catch {
      reservaGuardada = null;
    }

    // Se saca el token de la URL, así recargar la página no repite nada.
    this.router.navigate([], { relativeTo: this.route, queryParams: {}, replaceUrl: true });

    this.verificandoPagoMp.set(true);
    this.api.verificarPagoSenia(token, pagoId).subscribe({
      next: ({ estado }) => {
        this.verificandoPagoMp.set(false);
        if (estado !== 'pendiente') {
          try { sessionStorage.removeItem(CLAVE_RESERVA_PENDIENTE_MP); } catch { /* sin storage */ }
        }

        if (estado === 'confirmado') {
          if (reservaGuardada) {
            this.reservaHecha.set(reservaGuardada);
            this.reservaConfirmada.set(true);
          } else {
            this.avisoPagoMp.set({ tipo: 'ok', texto: '¡Listo! Tu pago se aprobó y tu turno quedó reservado. Te mandamos la confirmación por email.' });
          }
        } else if (estado === 'pendiente' || statusUrl === 'pending' || statusUrl === 'in_process') {
          this.avisoPagoMp.set({ tipo: 'info', texto: 'Tu pago está en proceso. Cuando Mercado Pago lo apruebe, tu turno queda confirmado y te llega un email.' });
        } else if (estado === 'horario_ocupado') {
          this.avisoPagoMp.set({ tipo: 'info', texto: 'Recibimos tu pago, pero tu reserva se había vencido y ese horario ya lo tomó otra persona. El local te va a escribir para acordar otro horario o devolverte la seña.' });
        } else {
          // No pagó (volvió, cerró el checkout o se rechazó la tarjeta): se libera el horario
          // para que lo pueda volver a elegir.
          this.api.abandonarPagoSenia(token).subscribe({ next: () => this.buscarDisponibilidad(), error: () => {} });
          this.avisoPagoMp.set({ tipo: 'error', texto: 'El pago no se completó, así que el turno no quedó reservado. Podés elegir el horario y probar de nuevo.' });
        }
      },
      error: () => {
        this.verificandoPagoMp.set(false);
        this.avisoPagoMp.set({ tipo: 'info', texto: 'No pudimos revisar tu pago ahora. Si se aprobó, te llega el email de confirmación del turno.' });
      }
    });
  }

  private cargarSucursales(comercioId: number): void {
    this.api.getSucursales(comercioId).subscribe(sucursales => {
      this.sucursales.set(sucursales);
      const activas = sucursales.filter(s => s.activa);
      // Con 0 o 1 sucursal activa no hace falta que el cliente elija nada: se sigue
      // exactamente igual que antes de esta feature.
      if (activas.length <= 1) this.elegirSucursal(activas[0] ?? null);
    });
  }

  elegirSucursal(sucursal: Sucursal | null): void {
    this.sucursalSeleccionada.set(sucursal);
    const comercio = this.comercio();
    if (!comercio) return;

    this.cargarProfesionales(comercio.id, sucursal?.id ?? undefined);
    this.cargarServicios(comercio.id);
    this.elegirDia(this.diasDisponibles[0]);
  }

  private cargarProfesionales(comercioId: number, sucursalId?: number): void {
    this.api.getProfesionales(comercioId, sucursalId).subscribe(profesionales => {
      this.profesionales.set(profesionales);
      this.profesionalSeleccionado.set(null);
    });
  }

  elegirProfesional(profesional: Profesional | null): void {
    this.profesionalSeleccionado.set(profesional);
    this.slotSeleccionado.set(null);
    this.buscarDisponibilidad();
    this.cargarResumenDias();
  }

  private cargarServicios(comercioId: number): void {
    this.api.getServiciosPorComercio(comercioId).subscribe(servicios => {
      this.servicios.set(servicios);
      if (servicios.length > 0) this.elegirServicio(servicios[0]);
    });
  }

  elegirServicio(servicio: Servicio): void {
    this.servicioSeleccionado.set(servicio);
    this.slotSeleccionado.set(null);
    this.buscarDisponibilidad();
    this.cargarResumenDias();
  }

  elegirDia(dia: DiaGrilla): void {
    this.diaSeleccionado.set(dia);
    this.slotSeleccionado.set(null);
    this.buscarDisponibilidad();
  }

  elegirFechaManual(): void {
    if (!this.fechaManual) return;
    this.elegirDia(this.crearDiaGrilla(this.fechaManual));
  }

  private crearDiaGrilla(fechaIso: string): DiaGrilla {
    const [yyyy, mm, dd] = fechaIso.split('-').map(Number);
    const fecha = new Date(yyyy, mm - 1, dd);
    const diaSemana = fecha.getDay();
    return {
      fecha: fechaIso,
      etiquetaDia: DIAS_CORTOS[diaSemana],
      numeroDia: dd,
      etiquetaCompleta: `${DIAS_LARGOS[diaSemana]} ${dd}`
    };
  }

  private formatearFechaISO(d: Date): string {
    return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
  }

  elegirSlot(slot: SlotDisponibilidad): void {
    if (!slot.disponible) return;
    this.slotSeleccionado.set(slot);
    this.errorReserva.set(null);
  }

  private buscarDisponibilidad(): void {
    const comercio = this.comercio();
    const servicio = this.servicioSeleccionado();
    const dia = this.diaSeleccionado();
    if (!comercio || !servicio || !dia) return;

    this.cargandoSlots.set(true);
    this.api.getDisponibilidad(comercio.id, servicio.id, dia.fecha, this.profesionalSeleccionado()?.id, this.sucursalSeleccionada()?.id).subscribe({
      next: slots => {
        this.slots.set(slots);
        this.cargandoSlots.set(false);
      },
      error: () => {
        this.slots.set([]);
        this.cargandoSlots.set(false);
      }
    });
  }

  private cargarResumenDias(): void {
    const comercio = this.comercio();
    const servicio = this.servicioSeleccionado();
    if (!comercio || !servicio) return;

    // Si el cliente cambia de servicio o profesional rápido, se descartan las respuestas viejas.
    const consulta = ++this.consultaResumenDias;
    const profesionalId = this.profesionalSeleccionado()?.id;
    const sucursalId = this.sucursalSeleccionada()?.id;
    forkJoin(this.diasDisponibles.map(d =>
      this.api.getDisponibilidad(comercio.id, servicio.id, d.fecha, profesionalId, sucursalId).pipe(catchError(() => of(null)))
    )).subscribe(resultados => {
      if (consulta !== this.consultaResumenDias) return;
      const resumen: Record<string, { total: number; libres: number }> = {};
      resultados.forEach((slots, i) => {
        if (slots) resumen[this.diasDisponibles[i].fecha] = { total: slots.length, libres: slots.filter(x => x.disponible).length };
      });
      this.resumenDias.set(resumen);
    });
  }

  diaCerrado(d: DiaGrilla): boolean {
    return this.resumenDias()[d.fecha]?.total === 0;
  }

  textoLibresDia(d: DiaGrilla): string {
    const r = this.resumenDias()[d.fecha];
    if (!r) return '';
    if (r.total === 0) return 'cerrado';
    return r.libres === 0 ? 'completo' : `${r.libres} libre${r.libres === 1 ? '' : 's'}`;
  }

  esFechaManualSeleccionada(): boolean {
    const dia = this.diaSeleccionado();
    return !!dia && !this.diasDisponibles.some(d => d.fecha === dia.fecha);
  }

  // Horarios agrupados en Mañana (antes de las 13:00) y Tarde.
  gruposDeSlots(): { nombre: string; slots: SlotDisponibilidad[]; libres: number }[] {
    const maniana = this.slots().filter(s => new Date(s.inicio).getHours() < 13);
    const tarde = this.slots().filter(s => new Date(s.inicio).getHours() >= 13);
    return [
      { nombre: 'Mañana', slots: maniana, libres: maniana.filter(s => s.disponible).length },
      { nombre: 'Tarde', slots: tarde, libres: tarde.filter(s => s.disponible).length }
    ];
  }

  // Barra de pasos: ✓ los completos y resaltado el primero que falta.
  pasos(): { nombre: string; estado: 'hecho' | 'actual' | 'pendiente' }[] {
    const completos = [
      { nombre: 'Local', hecho: !this.necesitaElegirSucursal() || !!this.sucursalSeleccionada() },
      { nombre: 'Profesional', hecho: true },
      { nombre: 'Servicio', hecho: !!this.servicioSeleccionado() },
      { nombre: 'Día y hora', hecho: !!this.slotSeleccionado() },
      { nombre: 'Tus datos', hecho: this.formularioValido() }
    ];
    const primeroPendiente = completos.findIndex(p => !p.hecho);
    return completos.map((p, i) => ({ nombre: p.nombre, estado: p.hecho ? 'hecho' : i === primeroPendiente ? 'actual' : 'pendiente' }));
  }

  fotoDe(p: Profesional): string | null {
    return urlArchivo(p.fotoUrl);
  }

  inicialesDe(nombre: string): string {
    return nombre.trim().split(/\s+/).filter(Boolean).slice(0, 2).map(p => p[0].toUpperCase()).join('') || '?';
  }

  formatearHora(iso: string): string {
    const d = new Date(iso);
    return d.toLocaleTimeString('es-AR', { hour: '2-digit', minute: '2-digit', hour12: false });
  }

  copiarLink(): void {
    const url = `${this.hostActual}/${this.comercio()?.aliasUrl ?? ''}`;
    navigator.clipboard?.writeText(url).then(() => {
      this.linkCopiado.set(true);
      setTimeout(() => this.linkCopiado.set(false), 1600);
    });
  }

  // Link wa.me al WhatsApp del comercio. Mismo formato que usa el backend para enviar
  // WhatsApp (FormatearNumeroWhatsApp): solo dígitos, con 54 y el 9 de celular adelante.
  linkWhatsAppComercio(): string | null {
    const telefono = this.comercio()?.telefonoNotificaciones ?? '';
    let numero = telefono.replace(/\D/g, '');
    if (!numero) return null;
    if (!numero.startsWith('54')) numero = '54' + numero;
    if (numero[2] !== '9') numero = numero.slice(0, 2) + '9' + numero.slice(2);
    return `https://wa.me/${numero}`;
  }

  confirmarReserva(): void {
    this.intentoEnviar.set(true);

    const comercio = this.comercio();
    const servicio = this.servicioSeleccionado();
    const slot = this.slotSeleccionado();
    if (!comercio || !servicio || !slot || !this.puedeReservar()) return;

    this.reservando.set(true);
    this.errorReserva.set(null);
    const reservaHecha = this.armarReservaHecha();

    this.api.crearTurno({
      comercioId: comercio.id,
      servicioId: servicio.id,
      fechaHoraInicio: slot.inicio,
      clienteNombre: this.clienteNombre.trim(),
      clienteWhatsApp: this.clienteWhatsApp.trim(),
      clienteEmail: this.clienteEmail.trim(),
      profesionalId: this.profesionalSeleccionado()?.id ?? null,
      comprobanteBase64: this.pideSenia() && !this.seniaPorMercadoPago() ? this.comprobanteBase64() : null,
      medioSenia: this.pideSenia() ? (this.seniaPorMercadoPago() ? 'MercadoPago' : 'Transferencia') : null
    }).subscribe({
      next: respuesta => {
        if (respuesta.urlPago) {
          // El turno queda apartado mientras paga; la confirmación se muestra a la vuelta.
          try { sessionStorage.setItem(CLAVE_RESERVA_PENDIENTE_MP, JSON.stringify(reservaHecha)); } catch { /* sin storage */ }
          window.location.href = respuesta.urlPago;
          return;
        }
        this.reservando.set(false);
        this.reservaHecha.set(reservaHecha);
        this.reservaConfirmada.set(true);
        if (typeof window !== 'undefined') window.scrollTo({ top: 0, behavior: 'smooth' });
        this.cargarResumenDias();
      },
      error: err => {
        this.reservando.set(false);
        if (err.status === 409) {
          this.errorReserva.set(err.error?.mensaje ?? 'Ese horario ya no está disponible. Elegí otro.');
          this.buscarDisponibilidad();
          this.cargarResumenDias();
          this.slotSeleccionado.set(null);
        } else if (err.status === 400 && err.error?.mensaje) {
          this.errorReserva.set(err.error.mensaje);
        } else {
          this.errorReserva.set('No pudimos guardar la reserva. Probá de nuevo.');
        }
      }
    });
  }

  reservarOtroTurno(): void {
    this.reservaConfirmada.set(false);
    this.reservaHecha.set(null);
    this.slotSeleccionado.set(null);
    this.clienteNombre = '';
    this.clienteWhatsApp = '';
    this.clienteEmail = '';
    this.intentoEnviar.set(false);
    this.camposTocados.set(new Set());
    this.avisoPagoMp.set(null);
    this.quitarComprobante();
    this.buscarDisponibilidad();
  }

  private generarProximosDias(cantidad: number): DiaGrilla[] {
    const dias: DiaGrilla[] = [];
    const hoy = new Date();

    for (let i = 0; i < cantidad; i++) {
      const fecha = new Date(hoy);
      fecha.setDate(hoy.getDate() + i);

      const yyyy = fecha.getFullYear();
      const mm = String(fecha.getMonth() + 1).padStart(2, '0');
      const dd = String(fecha.getDate()).padStart(2, '0');
      const diaSemana = fecha.getDay();

      dias.push({
        fecha: `${yyyy}-${mm}-${dd}`,
        etiquetaDia: DIAS_CORTOS[diaSemana],
        numeroDia: fecha.getDate(),
        etiquetaCompleta: `${DIAS_LARGOS[diaSemana]} ${fecha.getDate()}`
      });
    }

    return dias;
  }
}
