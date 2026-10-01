import { Component, OnInit, signal, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { catchError, forkJoin, of } from 'rxjs';

import { Api, ComercioPublico, Servicio, SlotDisponibilidad, Sucursal, Profesional, urlArchivo } from '../../core/api';

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

  pideSenia(): boolean {
    return (this.servicioSeleccionado()?.['montoSeña'] ?? 0) > 0;
  }

  puedeReservar(): boolean {
    return !!this.servicioSeleccionado() &&
      !!this.slotSeleccionado() &&
      this.slotSeleccionado()!.disponible &&
      this.formularioValido() &&
      (!this.pideSenia() || !!this.comprobanteBase64());
  }

  // El botón dice lo que falta, en el orden en que se completa el formulario.
  textoBotonReservar(): string {
    if (!this.servicioSeleccionado()) return 'Elegí un servicio';
    if (!this.slotSeleccionado()) return 'Elegí un horario';
    if (this.clienteNombre.trim().length <= 1) return 'Completá tu nombre';
    const whatsapp = this.clienteWhatsApp.trim();
    if (!WHATSAPP_REGEX.test(whatsapp) || (whatsapp.match(/\d/g) ?? []).length < 8) return 'Completá tu WhatsApp';
    if (!EMAIL_REGEX.test(this.clienteEmail.trim())) return 'Completá tu email';
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

  // Texto de la pantalla de confirmación (se rediseña en la Etapa 6). Ya no hay pre-reserva
  // ni plazo de 2 horas: el turno queda reservado y la confirmación llega por mail.
  mensajeConfirmacion(): string {
    return this.pideSenia()
      ? 'Te enviamos la confirmación por mail. El local va a verificar el comprobante de tu seña.'
      : 'Te enviamos la confirmación de tu turno por mail.';
  }

  constructor(private route: ActivatedRoute, private api: Api) {}

  ngOnInit(): void {
    const alias = this.route.snapshot.paramMap.get('alias');
    if (!alias) {
      this.cargandoComercio.set(false);
      return;
    }

    this.api.getComercioPorAlias(alias).subscribe({
      next: comercio => {
        this.comercio.set(comercio);
        this.cargandoComercio.set(false);
        this.cargarSucursales(comercio.id);
      },
      error: err => {
        if (err.status === 403) this.comercioInactivo.set(true);
        this.cargandoComercio.set(false);
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

    this.api.crearTurno({
      comercioId: comercio.id,
      servicioId: servicio.id,
      fechaHoraInicio: slot.inicio,
      clienteNombre: this.clienteNombre.trim(),
      clienteWhatsApp: this.clienteWhatsApp.trim(),
      clienteEmail: this.clienteEmail.trim(),
      profesionalId: this.profesionalSeleccionado()?.id ?? null,
      comprobanteBase64: this.pideSenia() ? this.comprobanteBase64() : null
    }).subscribe({
      next: () => {
        this.reservando.set(false);
        this.reservaConfirmada.set(true);
        this.cargarResumenDias();
      },
      error: err => {
        this.reservando.set(false);
        if (err.status === 409) {
          this.errorReserva.set('Ese horario ya no está disponible. Elegí otro.');
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
    this.slotSeleccionado.set(null);
    this.clienteNombre = '';
    this.clienteWhatsApp = '';
    this.clienteEmail = '';
    this.intentoEnviar.set(false);
    this.camposTocados.set(new Set());
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
