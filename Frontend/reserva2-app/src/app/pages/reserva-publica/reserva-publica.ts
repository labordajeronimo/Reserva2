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

  errorNombre(): string | null {
    if (!this.intentoEnviar()) return null;
    return this.clienteNombre.trim().length > 1 ? null : 'Ingresá tu nombre.';
  }

  errorWhatsApp(): string | null {
    if (!this.intentoEnviar()) return null;
    const valor = this.clienteWhatsApp.trim();
    if (valor.length === 0) return 'Ingresá tu WhatsApp.';
    const cantidadDigitos = (valor.match(/\d/g) ?? []).length;
    if (!WHATSAPP_REGEX.test(valor) || cantidadDigitos < 8) {
      return 'Ingresá un WhatsApp válido, con código de área (ej: 341 000 0000).';
    }
    return null;
  }

  errorEmail(): string | null {
    if (!this.intentoEnviar()) return null;
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

  puedeReservar(): boolean {
    return !!this.servicioSeleccionado() &&
      !!this.slotSeleccionado() &&
      this.slotSeleccionado()!.disponible &&
      this.formularioValido();
  }

  // El texto de la pantalla de confirmación depende de si ESTE comercio tiene el bot de
  // WhatsApp activado (exclusivo Premium) y si EL SERVICIO elegido tiene seña configurada —
  // no todos los comercios ni todos los servicios tienen ninguna de las dos cosas, así que
  // no hay que prometer ninguna que no vaya a pasar.
  mensajeConfirmacion(): string {
    const whatsAppActivo = this.comercio()?.whatsAppActivo ?? false;
    const tieneSenia = !!this.servicioSeleccionado()?.['montoSeña'];

    if (whatsAppActivo && tieneSenia) {
      return `Te vamos a escribir por WhatsApp al ${this.clienteWhatsApp} con el alias para la seña. Tenés 2 horas para transferir antes de que el horario se libere.`;
    }
    if (!whatsAppActivo && tieneSenia) {
      return 'Te enviamos por mail los datos para la seña. Tenés 2 horas para transferir antes de que el horario se libere.';
    }
    if (whatsAppActivo && !tieneSenia) {
      return `Te vamos a escribir por WhatsApp al ${this.clienteWhatsApp} para confirmar tu turno.`;
    }
    return 'Te enviamos la confirmación de tu turno por mail.';
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
      profesionalId: this.profesionalSeleccionado()?.id ?? null
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
